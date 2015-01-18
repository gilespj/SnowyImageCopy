﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Media;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Threading;

using DesktopToast;
using SnowyImageCopy.Common;
using SnowyImageCopy.Helper;
using SnowyImageCopy.Models.Exceptions;
using SnowyImageCopy.Models.Network;
using SnowyImageCopy.Properties;
using SnowyImageCopy.ViewModels;

namespace SnowyImageCopy.Models
{
	/// <summary>
	/// Run operations.
	/// </summary>
	public class Operation : NotificationObject
	{
		/// <summary>
		/// Instance of MainWindowViewModel
		/// </summary>
		private MainWindowViewModel MainWindowViewModelInstance
		{
			get { return _mainWindowViewModelInstance; }
			set
			{
				if ((_mainWindowViewModelInstance != null) || (value == null))
					return;

				_mainWindowViewModelInstance = value;

				if (!Designer.IsInDesignMode) // ListCollectionView may be null in Design mode.
				{
					FileListCoreView.CurrentChanged += (sender, e) => CheckFileListCoreViewThumbnail();
				}
			}
		}
		private MainWindowViewModel _mainWindowViewModelInstance;

		public Operation(MainWindowViewModel mainWindowViewModelInstance)
		{
			MainWindowViewModelInstance = mainWindowViewModelInstance;
		}


		#region Access to MainWindowViewModel member

		private string OperationStatus
		{
			set { MainWindowViewModelInstance.OperationStatus = value; }
		}

		private FileItemViewModelCollection FileListCore
		{
			get { return MainWindowViewModelInstance.FileListCore; }
		}

		private ListCollectionView FileListCoreView
		{
			get { return MainWindowViewModelInstance.FileListCoreView; }
		}

		private int FileListCoreViewIndex
		{
			set { MainWindowViewModelInstance.FileListCoreViewIndex = value; }
		}

		private FileItemViewModel CurrentItem
		{
			set { MainWindowViewModelInstance.CurrentItem = value; }
		}

		private byte[] CurrentImageData
		{
			get { return MainWindowViewModelInstance.CurrentImageData; }
			set { MainWindowViewModelInstance.CurrentImageData = value; }
		}

		private bool IsWindowActivateRequested
		{
			set { MainWindowViewModelInstance.IsWindowActivateRequested = value; }
		}

		#endregion


		#region Constant

		/// <summary>
		/// Waiting time length for showing status in case of failure during auto check
		/// </summary>
		private readonly TimeSpan autoWaitingLength = TimeSpan.FromSeconds(5);

		/// <summary>
		/// Threshold time length of intervals to actually check files during auto check
		/// </summary>
		private readonly TimeSpan autoThresholdLength = TimeSpan.FromMinutes(10);

		/// <summary>
		/// Waiting time length for showing completion of copying
		/// </summary>
		private readonly TimeSpan copyWaitingLength = TimeSpan.FromSeconds(0.2);

		/// <summary>
		/// Threshold time length of copying to determine whether to send a toast notification after copy
		/// </summary>
		private readonly TimeSpan toastThresholdLength = TimeSpan.FromSeconds(30);

		#endregion


		#region Operation state

		/// <summary>
		/// Checking files in FlashAir card.
		/// </summary>
		public bool IsChecking
		{
			get { return _isChecking; }
			set
			{
				_isChecking = value;
				RaisePropertyChanged();
			}
		}
		private bool _isChecking;

		/// <summary>
		/// Copying files from FlashAir card.
		/// </summary>
		public bool IsCopying
		{
			get { return _isCopying; }
			set
			{
				_isCopying = value;
				RaisePropertyChanged();
			}
		}
		private bool _isCopying;

		/// <summary>
		/// Running timer for auto check
		/// </summary>
		internal bool IsAutoRunning
		{
			get { return _isAutoRunning; }
			set
			{
				_isAutoRunning = value;
				RaisePropertyChanged();
			}
		}
		private bool _isAutoRunning;

		/// <summary>
		/// Progress of operation
		/// </summary>
		public ProgressInfo OperationProgress
		{
			get { return _operationProgress; }
			set
			{
				_operationProgress = value;
				RaisePropertyChanged();
			}
		}
		private ProgressInfo _operationProgress;

		#endregion


		#region Auto check

		private DispatcherTimer autoTimer;

		private bool isFileListCoreViewThumbnailFilled;

		private void CheckFileListCoreViewThumbnail()
		{
			if (IsChecking)
				return;

			isFileListCoreViewThumbnailFilled = FileListCore
				.Where(x => x.IsTarget && (x.Size != 0))
				.All(x => x.HasThumbnail || (!(x.IsAliveRemote && x.CanGetThumbnailRemote) && !(x.IsAliveLocal && x.CanLoadDataLocal)));
		}

		public void StartAutoTimer()
		{
			CheckFileListCoreViewThumbnail();

			IsAutoRunning = true;
			ResetAutoTimer();
		}

		private void StopAutoTimer()
		{
			IsAutoRunning = false;
			ResetAutoTimer();
		}

		public void ResetAutoTimer()
		{
			if (IsAutoRunning)
			{
				if (autoTimer == null)
				{
					autoTimer = new DispatcherTimer();
					autoTimer.Tick += OnAutoTimerTick;
				}

				autoTimer.Stop();
				autoTimer.Interval = TimeSpan.FromSeconds(Settings.Current.AutoCheckInterval);
				autoTimer.Start();
				OperationStatus = Resources.OperationStatus_WaitingAutoCheck;
			}
			else
			{
				if (autoTimer == null)
					return;

				autoTimer.Stop();
				autoTimer = null;
				SystemSounds.Asterisk.Play();
				OperationStatus = Resources.OperationStatus_Stopped;
			}
		}

		private async void OnAutoTimerTick(object sender, EventArgs e)
		{
			if (IsChecking || IsCopying)
				return;

			autoTimer.Stop();

			if (!await ExecuteAutoCheckAsync())
				await Task.Delay(autoWaitingLength);

			if (IsAutoRunning)
			{
				autoTimer.Start();
				OperationStatus = Resources.OperationStatus_WaitingAutoCheck;
			}
		}

		/// <summary>
		/// Execute auto check by timer.
		/// </summary>
		/// <returns>False if failed</returns>
		private async Task<bool> ExecuteAutoCheckAsync()
		{
			if (isFileListCoreViewThumbnailFilled &&
				(DateTime.Now < LastCheckCopyTime.Add(autoThresholdLength)))
			{
				var isUpdated = await CheckUpdateAsync();
				if (!isUpdated.HasValue)
					return false;
				if (!isUpdated.Value)
					return true; // This is true case.
			}

			var hasCompleted = await CheckCopyFileAsync();
			if (hasCompleted)
				isFileListCoreViewThumbnailFilled = true;

			return hasCompleted;
		}

		#endregion


		#region Check & Copy

		private readonly CardInfo card = new CardInfo();

		private CancellationTokenSource tokenSourceWorking;
		private bool isTokenSourceWorkingDisposed;

		private CancellationTokenSource tokenSourceLoading;
		private bool isTokenSourceLoadingDisposed;

		private DateTime LastCheckCopyTime { get; set; }

		internal DateTime CopyStartTime { get; private set; }
		private int countFileCopied;


		#region 1st tier

		/// <summary>
		/// Check if content of FlashAir card is updated.
		/// </summary>
		/// <returns>
		/// True:  If completed and content found updated.
		/// False: If completed and content found not updated.
		/// Null:  If failed.
		/// </returns>
		private async Task<bool?> CheckUpdateAsync()
		{
			try
			{
				if (await NetworkChecker.IsNetworkConnectedAsync(card))
				{
					OperationStatus = Resources.OperationStatus_Checking;

					var isUpdated = card.CanGetWriteTimeStamp
						? (await FileManager.GetWriteTimeStampAsync() != card.WriteTimeStamp)
						: await FileManager.CheckUpdateStatusAsync();

					OperationStatus = Resources.OperationStatus_Completed;
					return isUpdated;
				}
				else
				{
					OperationStatus = Resources.OperationStatus_ConnectionUnable;
					return false;
				}
			}
			catch (Exception ex)
			{
				if (ex.GetType() == typeof(RemoteConnectionUnableException))
				{
					OperationStatus = Resources.OperationStatus_ConnectionUnable;
				}
				else if (ex.GetType() == typeof(RemoteConnectionLostException))
				{
					OperationStatus = Resources.OperationStatus_ConnectionLost;
				}
				else if (ex.GetType() == typeof(TimeoutException))
				{
					OperationStatus = Resources.OperationStatus_TimedOut;
				}
				else
				{
					OperationStatus = Resources.OperationStatus_Error;
					Debug.WriteLine("Failed to check if content is updated. {0}", ex);
					throw new UnexpectedException("Failed to check if content is updated.", ex);
				}
			}

			return null;
		}

		/// <summary>
		/// Check & Copy files in FlashAir card.
		/// </summary>
		/// <returns>
		/// True:  If completed.
		/// False: If interrupted or failed.
		/// </returns>
		/// <remarks>This method is called by Command or timer.</remarks>
		public async Task<bool> CheckCopyFileAsync()
		{
			if (!IsReady())
				return true; // This is true case.

			try
			{
				IsChecking = true;
				IsCopying = true;

				await CheckFileBaseAsync();

				OperationProgress = null;

				LastCheckCopyTime = CheckFileToBeCopied(true)
					? DateTime.MinValue
					: DateTime.Now;

				await CopyFileBaseAsync(new Progress<ProgressInfo>(x => OperationProgress = x));

				LastCheckCopyTime = DateTime.Now;

				await Task.Delay(copyWaitingLength);
				OperationProgress = null;

				IsChecking = false;
				IsCopying = false;

				await ShowToastAsync();

				return true;
			}
			catch (OperationCanceledException)
			{
				SystemSounds.Asterisk.Play();
				OperationStatus = Resources.OperationStatus_Stopped;
			}
			catch (Exception ex)
			{
				if (!IsAutoRunning)
					SystemSounds.Hand.Play();

				if (ex.GetType() == typeof(RemoteConnectionUnableException))
				{
					OperationStatus = Resources.OperationStatus_ConnectionUnable;
				}
				else if (ex.GetType() == typeof(RemoteConnectionLostException))
				{
					OperationStatus = Resources.OperationStatus_ConnectionLost;
				}
				else if (ex.GetType() == typeof(TimeoutException))
				{
					OperationStatus = Resources.OperationStatus_TimedOut;
				}
				else if (ex.GetType() == typeof(UnauthorizedAccessException))
				{
					OperationStatus = Resources.OperationStatus_UnauthorizedAccess;
				}
				else if (ex.GetType() == typeof(CardChangedException))
				{
					OperationStatus = Resources.OperationStatus_NotSameFlashAir;
				}
				else if (ex.GetType() == typeof(CardUploadDisabledException))
				{
					OperationStatus = Resources.OperationStatus_DeleteDisabled;
				}
				else if (ex.GetType() == typeof(RemoteFileDeletionFailedException))
				{
					OperationStatus = Resources.OperationStatus_DeleteFailed;
				}
				else
				{
					OperationStatus = Resources.OperationStatus_Error;
					Debug.WriteLine("Failed to check & copy files. {0}", ex);
					throw new UnexpectedException("Failed to check & copy files.", ex);
				}
			}
			finally
			{
				// In case any exception happened.
				IsChecking = false;
				IsCopying = false;
			}

			return false;
		}

		/// <summary>
		/// Check files in FlashAir card.
		/// </summary>
		/// <remarks>This method is called by Command.</remarks>
		public async Task CheckFileAsync()
		{
			if (!IsReady())
				return;

			try
			{
				IsChecking = true;

				await CheckFileBaseAsync();

				OperationProgress = null;

				LastCheckCopyTime = CheckFileToBeCopied(false)
					? DateTime.MinValue
					: DateTime.Now;
			}
			catch (OperationCanceledException)
			{
				SystemSounds.Asterisk.Play();
				OperationStatus = Resources.OperationStatus_Stopped;
			}
			catch (Exception ex)
			{
				SystemSounds.Hand.Play();

				if (ex.GetType() == typeof(RemoteConnectionUnableException))
				{
					OperationStatus = Resources.OperationStatus_ConnectionUnable;
				}
				else if (ex.GetType() == typeof(RemoteConnectionLostException))
				{
					OperationStatus = Resources.OperationStatus_ConnectionLost;
				}
				else if (ex.GetType() == typeof(TimeoutException))
				{
					OperationStatus = Resources.OperationStatus_TimedOut;
				}
				else
				{
					OperationStatus = Resources.OperationStatus_Error;
					Debug.WriteLine("Failed to check files. {0}", ex);
					throw new UnexpectedException("Failed to check files.", ex);
				}
			}
			finally
			{
				IsChecking = false;
			}
		}

		/// <summary>
		/// Copy files from FlashAir card.
		/// </summary>
		/// <remarks>This method is called by Command.</remarks>
		public async Task CopyFileAsync()
		{
			if (!IsReady())
				return;

			try
			{
				IsCopying = true;

				OperationProgress = null;

				await CopyFileBaseAsync(new Progress<ProgressInfo>(x => OperationProgress = x));

				await Task.Delay(copyWaitingLength);
				OperationProgress = null;

				IsCopying = false;

				await ShowToastAsync();
			}
			catch (OperationCanceledException)
			{
				SystemSounds.Asterisk.Play();
				OperationStatus = Resources.OperationStatus_Stopped;
			}
			catch (Exception ex)
			{
				SystemSounds.Hand.Play();

				if (ex.GetType() == typeof(RemoteConnectionUnableException))
				{
					OperationStatus = Resources.OperationStatus_ConnectionUnable;
				}
				else if (ex.GetType() == typeof(RemoteConnectionLostException))
				{
					OperationStatus = Resources.OperationStatus_ConnectionLost;
				}
				else if (ex.GetType() == typeof(TimeoutException))
				{
					OperationStatus = Resources.OperationStatus_TimedOut;
				}
				else if (ex.GetType() == typeof(UnauthorizedAccessException))
				{
					OperationStatus = Resources.OperationStatus_UnauthorizedAccess;
				}
				else if (ex.GetType() == typeof(CardChangedException))
				{
					OperationStatus = Resources.OperationStatus_NotSameFlashAir;
				}
				else if (ex.GetType() == typeof(CardUploadDisabledException))
				{
					OperationStatus = Resources.OperationStatus_DeleteDisabled;
				}
				else if (ex.GetType() == typeof(RemoteFileDeletionFailedException))
				{
					OperationStatus = Resources.OperationStatus_DeleteFailed;
				}
				else
				{
					OperationStatus = Resources.OperationStatus_Error;
					Debug.WriteLine("Failed to copy files. {0}", ex);
					throw new UnexpectedException("Failed to copy files.", ex);
				}
			}
			finally
			{
				// In case any exception happened.
				IsCopying = false;
			}
		}

		/// <summary>
		/// Stop operation.
		/// </summary>
		/// <remarks>This method is called by Command.</remarks>
		public void Stop()
		{
			StopAutoTimer();

			if (isTokenSourceWorkingDisposed || (tokenSourceWorking == null) || tokenSourceWorking.IsCancellationRequested)
				return;

			try
			{
				tokenSourceWorking.Cancel();
			}
			catch (ObjectDisposedException ode)
			{
				Debug.WriteLine("CancellationTokenSource has been disposed when tried to cancel operation. {0}", ode);
			}
		}

		/// <summary>
		/// Load image data from local file of a specified item and set it to current image data.
		/// </summary>
		/// <param name="item">Target item</param>
		internal async Task LoadSetFileAsync(FileItemViewModel item)
		{
			if (!isTokenSourceLoadingDisposed && (tokenSourceLoading != null) && !tokenSourceLoading.IsCancellationRequested)
			{
				try
				{
					tokenSourceLoading.Cancel();
				}
				catch (ObjectDisposedException ode)
				{
					Debug.WriteLine("CancellationTokenSource has been disposed when tried to cancel operation. {0}", ode);
				}
			}

			try
			{
				await LoadSetFileBaseAsync(item);
			}
			catch (OperationCanceledException)
			{
				// None.
			}
			catch (Exception ex)
			{
				Debug.WriteLine("Failed to load image data from local file. {0}", ex);
				throw new UnexpectedException("Failed to load image data from local file.", ex);
			}
		}

		#endregion


		#region 2nd tier

		/// <summary>
		/// Check if ready for operation.
		/// </summary>
		/// <returns>True if ready</returns>
		private bool IsReady()
		{
			if (!NetworkChecker.IsNetworkConnected())
			{
				OperationStatus = Resources.OperationStatus_NoNetwork;
				return false;
			}

			if ((Settings.Current.TargetPeriod == FilePeriod.Select) &&
				!Settings.Current.TargetDates.Any())
			{
				OperationStatus = Resources.OperationStatus_NoDateCalender;
				return false;
			}

			return true;
		}

		/// <summary>
		/// Check files in FlashAir card.
		/// </summary>
		private async Task CheckFileBaseAsync()
		{
			OperationStatus = Resources.OperationStatus_Checking;

			try
			{
				tokenSourceWorking = new CancellationTokenSource();
				isTokenSourceWorkingDisposed = false;

				// Check firmware version.
				card.FirmwareVersion = await FileManager.GetFirmwareVersionAsync(tokenSourceWorking.Token);

				// Check CID.
				if (card.CanGetCid)
					card.Cid = await FileManager.GetCidAsync(tokenSourceWorking.Token);

				// Check SSID.
				card.Ssid = await FileManager.GetSsidAsync(tokenSourceWorking.Token);
				if (!String.IsNullOrWhiteSpace(card.Ssid))
				{
					// Check if PC is connected to FlashAir card by wireless network.
					var checkTask = Task.Run(async () =>
						card.IsWirelessConnected = await NetworkChecker.IsWirelessNetworkConnectedAsync(card.Ssid));
				}

				// Get all items.
				var fileListNew = await FileManager.GetFileListRootAsync(tokenSourceWorking.Token, card);
				fileListNew.Sort();

				// Record time stamp of write event.
				if (card.CanGetWriteTimeStamp)
					card.WriteTimeStamp = await FileManager.GetWriteTimeStampAsync(tokenSourceWorking.Token);

				// Check if any sample is in old items.
				var isSample = FileListCore.Any(x => x.Size == 0);

				// Check if FlashAir card is changed.
				bool isChanged;
				if (card.IsChanged.HasValue)
				{
					isChanged = card.IsChanged.Value;
				}
				else
				{
					var signatures = FileListCore.Select(x => x.Signature).ToArray();
					isChanged = !fileListNew.Select(x => x.Signature).Any(x => signatures.Contains(x));
				}

				if (isSample || isChanged)
					FileListCore.Clear();

				// Check old items.
				foreach (var itemOld in FileListCore)
				{
					var itemBuff = fileListNew.FirstOrDefault(x => x.FilePath == itemOld.FilePath);
					if ((itemBuff != null) && (itemBuff.Size == itemOld.Size))
					{
						fileListNew.Remove(itemBuff);

						itemOld.IsAliveRemote = true;
						itemOld.IsAliveLocal = IsCopiedLocal(itemOld);
						itemOld.Status = itemOld.IsAliveLocal ? FileStatus.Copied : FileStatus.NotCopied;
						continue;
					}

					itemOld.IsAliveRemote = false;
				}

				// Add new items.
				var isLeadOff = true;
				foreach (var itemNew in fileListNew)
				{
					if (isLeadOff)
					{
						FileListCoreViewIndex = FileListCoreView.IndexOf(itemNew);
						isLeadOff = false;
					}

					itemNew.IsAliveRemote = true;
					itemNew.IsAliveLocal = IsCopiedLocal(itemNew);
					itemNew.Status = itemNew.IsAliveLocal ? FileStatus.Copied : FileStatus.NotCopied;

					FileListCore.Insert(itemNew); // Customized Insert method
				}

				// Manage deleted items.
				var itemsDeleted = FileListCore.Where(x => !x.IsAliveRemote && (x.Status != FileStatus.Recycled)).ToArray();
				if (itemsDeleted.Any())
				{
					if (Settings.Current.MovesFileToRecycle)
					{
						var itemsDeletedCopied = itemsDeleted.Where(x => x.Status == FileStatus.Copied).ToList();

						Recycle.MoveToRecycle(itemsDeletedCopied.Select(ComposeLocalPath));

						itemsDeletedCopied.ForEach(x => x.Status = FileStatus.Recycled);
					}

					for (int i = itemsDeleted.Length - 1; i >= 0; i--)
					{
						if ((itemsDeleted[i].Status == FileStatus.Copied) ||
							(itemsDeleted[i].Status == FileStatus.Recycled))
							continue;

						FileListCore.Remove(itemsDeleted[i]);
					}
				}

				// Get thumbnails (from local).
				foreach (var item in FileListCore)
				{
					if (!item.IsTarget || item.HasThumbnail || (item.Status != FileStatus.Copied) || !item.IsAliveLocal || !item.CanLoadDataLocal)
						continue;

					tokenSourceWorking.Token.ThrowIfCancellationRequested();

					try
					{
						if (item.CanReadExif)
							item.Thumbnail = await ImageManager.ReadThumbnailAsync(ComposeLocalPath(item));
						else if (item.CanLoadDataLocal)
							item.Thumbnail = await ImageManager.CreateThumbnailAsync(ComposeLocalPath(item));
					}
					catch (FileNotFoundException)
					{
						item.Status = FileStatus.NotCopied;
						item.IsAliveLocal = false;
					}
					catch (IOException)
					{
						item.CanLoadDataLocal = false;
					}
					catch (ImageNotSupportedException)
					{
						item.CanLoadDataLocal = false;
					}
				}

				// Get thumbnails (from remote).
				foreach (var item in FileListCore)
				{
					if (!item.IsTarget || item.HasThumbnail || (item.Status == FileStatus.Copied) || !item.IsAliveRemote || !item.CanGetThumbnailRemote)
						continue;

					if (!card.CanGetThumbnail)
						continue;

					tokenSourceWorking.Token.ThrowIfCancellationRequested();

					try
					{
						item.Thumbnail = await FileManager.GetThumbnailAsync(item.FilePath, tokenSourceWorking.Token, card);
					}
					catch (RemoteFileThumbnailFailedException)
					{
						item.CanGetThumbnailRemote = false;
						card.ThumbnailFailedPath = item.FilePath;
					}
				}

				OperationStatus = Resources.OperationStatus_CheckCompleted;
			}
			finally
			{
				FileListCoreViewIndex = -1; // No selection

				if (tokenSourceWorking != null)
				{
					isTokenSourceWorkingDisposed = true;
					tokenSourceWorking.Dispose();
				}
			}
		}

		/// <summary>
		/// Check files to be copied from FlashAir card.
		/// </summary>
		/// <param name="changesToBeCopied">Whether to change file status if a file meets conditions to be copied</param>
		/// <returns>True if a file to be copied is contained</returns>
		private bool CheckFileToBeCopied(bool changesToBeCopied)
		{
			bool containsToBeCopied = false;

			foreach (var item in FileListCore)
			{
				if (item.IsTarget && item.IsAliveRemote && (item.Status == FileStatus.NotCopied))
				{
					containsToBeCopied = true;

					if (changesToBeCopied)
						item.Status = FileStatus.ToBeCopied;
				}
			}

			return containsToBeCopied;
		}

		/// <summary>
		/// Copy files from FlashAir card.
		/// </summary>
		/// <param name="progress">Progress</param>
		private async Task CopyFileBaseAsync(IProgress<ProgressInfo> progress)
		{
			CopyStartTime = DateTime.Now;
			countFileCopied = 0;

			if (!FileListCore.Any(x => x.IsTarget && (x.Status == FileStatus.ToBeCopied)))
			{
				OperationStatus = Resources.OperationStatus_NoFileToBeCopied;
				return;
			}

			OperationStatus = Resources.OperationStatus_Copying;

			try
			{
				tokenSourceWorking = new CancellationTokenSource();
				isTokenSourceWorkingDisposed = false;

				// Check CID.
				if (card.CanGetCid)
				{
					if (await FileManager.GetCidAsync(tokenSourceWorking.Token) != card.Cid)
						throw new CardChangedException();
				}

				// Check if upload.cgi is disabled.
				if (Settings.Current.DeleteUponCopy && card.CanGetUpload)
				{
					card.Upload = await FileManager.GetUploadAsync(tokenSourceWorking.Token);
					if (card.IsUploadDisabled)
						throw new CardUploadDisabledException();
				}

				while (true)
				{
					tokenSourceWorking.Token.ThrowIfCancellationRequested();

					var item = FileListCore.FirstOrDefault(x => x.IsTarget && (x.Status == FileStatus.ToBeCopied));
					if (item == null)
						break; // Copy completed.

					try
					{
						item.Status = FileStatus.Copying;

						FileListCoreViewIndex = FileListCoreView.IndexOf(item);

						var localPath = ComposeLocalPath(item);

						var localDirectory = Path.GetDirectoryName(localPath);
						if (!Directory.Exists(localDirectory))
							Directory.CreateDirectory(localDirectory);

						var data = await FileManager.GetSaveFileAsync(item.FilePath, localPath, item.Size, item.Date, item.CanReadExif, progress, tokenSourceWorking.Token, card);

						CurrentItem = item;
						CurrentImageData = data;

						if (!item.HasThumbnail)
						{
							try
							{
								if (item.CanReadExif)
									item.Thumbnail = await ImageManager.ReadThumbnailAsync(CurrentImageData);
								else if (item.CanLoadDataLocal)
									item.Thumbnail = await ImageManager.CreateThumbnailAsync(CurrentImageData);
							}
							catch (ImageNotSupportedException)
							{
								item.CanLoadDataLocal = false;
							}
						}

						item.CopiedTime = DateTime.Now;
						item.IsAliveLocal = true;
						item.Status = FileStatus.Copied;

						countFileCopied++;
					}
					catch (RemoteFileNotFoundException)
					{
						item.IsAliveRemote = false;
					}
					catch (RemoteFileInvalidException)
					{
						item.Status = FileStatus.Weird;
					}
					catch
					{
						item.Status = FileStatus.ToBeCopied; // Revert to status before copying.
						throw;
					}

					if (Settings.Current.DeleteUponCopy &&
						IsCopiedLocal(item))
					{
						await FileManager.DeleteFileAsync(item.FilePath, tokenSourceWorking.Token);
					}
				}

				OperationStatus = String.Format(Resources.OperationStatus_CopyCompleted, countFileCopied, (int)(DateTime.Now - CopyStartTime).TotalSeconds);
			}
			finally
			{
				FileListCoreViewIndex = -1; // No selection

				if (tokenSourceWorking != null)
				{
					isTokenSourceWorkingDisposed = true;
					tokenSourceWorking.Dispose();
				}
			}
		}

		/// <summary>
		/// Show a toast to notify copy completed.
		/// </summary>
		private async Task ShowToastAsync()
		{
			if (!OsVersion.IsEightOrNewer)
				return;

			if ((countFileCopied <= 0) || (DateTime.Now - CopyStartTime < toastThresholdLength))
				return;

			var request = new ToastRequest
			{
				ToastHeadline = Resources.ToastHeadline_CopyCompleted,
				ToastBody = Resources.ToastBody_CopyCompleted,
				ToastBodyExtra = String.Format(Resources.ToastBodyExtra_CopyCompleted, countFileCopied, (int)(DateTime.Now - CopyStartTime).TotalSeconds),
				ShortcutFileName = Properties.Settings.Default.ShortcutFileName,
				ShortcutTargetFilePath = Assembly.GetExecutingAssembly().Location,
				AppId = Properties.Settings.Default.AppId
			};

			var result = await ToastManager.ShowAsync(request);

			if (result == ToastResult.Activated)
				IsWindowActivateRequested = true; // Activating Window is requested.
		}

		/// <summary>
		/// Load image data from local file of a specified item and set it to current image data.
		/// </summary>
		/// <param name="item">Target item</param>
		private async Task LoadSetFileBaseAsync(FileItemViewModel item)
		{
			var localPath = ComposeLocalPath(item);

			try
			{
				tokenSourceLoading = new CancellationTokenSource();
				isTokenSourceLoadingDisposed = false;

				byte[] data = null;
				if (item.CanLoadDataLocal)
					data = await FileAddition.ReadAllBytesAsync(localPath, tokenSourceLoading.Token);

				CurrentItem = item;
				CurrentImageData = data;
			}
			catch (FileNotFoundException)
			{
				item.Status = FileStatus.NotCopied;
				item.IsAliveLocal = false;
			}
			catch (IOException)
			{
				item.CanLoadDataLocal = false;
			}
			finally
			{
				if (tokenSourceLoading != null)
				{
					isTokenSourceLoadingDisposed = true;
					tokenSourceLoading.Dispose();
				}
			}
		}

		#endregion

		#endregion


		#region Helper

		/// <summary>
		/// Compose path to local file of a specified item.
		/// </summary>
		/// <param name="item">Target item</param>
		private static string ComposeLocalPath(FileItemViewModel item)
		{
			var fileName = item.FileName;
			if (String.IsNullOrWhiteSpace((fileName)))
				throw new InvalidOperationException("FileName property is empty.");

			if (Settings.Current.MakesFileExtensionLowerCase)
			{
				var extension = Path.GetExtension(fileName);
				if (!String.IsNullOrEmpty(extension))
					fileName = Path.GetFileNameWithoutExtension(fileName) + extension.ToLower();
			}

			return Path.Combine(Settings.Current.LocalFolder, item.Date.ToString("yyyyMMdd"), fileName);
		}

		/// <summary>
		/// Check if local file of a specified item exists.
		/// </summary>
		/// <param name="item">Target item</param>
		private static bool IsCopiedLocal(FileItemViewModel item)
		{
			var localPath = ComposeLocalPath(item);

			return (File.Exists(localPath) && (new FileInfo(localPath).Length == item.Size));
		}

		#endregion
	}
}