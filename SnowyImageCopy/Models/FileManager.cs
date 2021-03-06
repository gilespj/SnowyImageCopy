﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

using SnowyImageCopy.Models.Exceptions;
using SnowyImageCopy.Models.Network;
using SnowyImageCopy.ViewModels;

namespace SnowyImageCopy.Models
{
	/// <summary>
	/// Manage FlashAir card.
	/// </summary>
	internal static class FileManager
	{
		#region Constant

		/// <summary>
		/// Timeout length for operation
		/// </summary>
		private static readonly TimeSpan timeoutLength = TimeSpan.FromSeconds(10);

		/// <summary>
		/// Interval length to monitor network connection during operation
		/// </summary>
		private static readonly TimeSpan monitorLength = TimeSpan.FromSeconds(2);

		/// <summary>
		/// Maximum count of retry 
		/// </summary>
		private const int retryCountMax = 3;

		/// <summary>
		/// Interval length of retry
		/// </summary>
		private static readonly TimeSpan retryLength = TimeSpan.FromMilliseconds(500);

		#endregion


		#region Method (Internal)

		/// <summary>
		/// Get a list of all files recursively from root of FlashAir card.
		/// </summary>
		/// <param name="token">CancellationToken</param>
		/// <param name="card">FlashAir card information</param>
		internal static async Task<List<FileItemViewModel>> GetFileListRootAsync(CancellationToken token, CardInfo card)
		{
			try
			{
				using (var client = new HttpClient() { Timeout = timeoutLength })
				{
					return await GetFileListAllAsync(client, String.Empty, token, card).ConfigureAwait(false);
				}
			}
			catch
			{
				Debug.WriteLine("Failed to get all file list.");
				throw;
			}
		}

		/// <summary>
		/// Get a list of all files recursively in a specified directory in FlashAir card.
		/// </summary>
		/// <param name="client">HttpClient</param>
		/// <param name="remoteDirectoryPath">Remote directory path</param>
		/// <param name="token">CancellationToken</param>
		/// <param name="card">FlashAir card information</param>
		/// <remarks>This method is part of parent method.</remarks>
		private static async Task<List<FileItemViewModel>> GetFileListAllAsync(HttpClient client, string remoteDirectoryPath, CancellationToken token, CardInfo card)
		{
			var itemList = await GetFileListEachAsync(client, remoteDirectoryPath, token, card);

			for (int i = itemList.Count - 1; 0 <= i; i--)
			{
				if (itemList[i].IsHidden || itemList[i].IsSystemFile || itemList[i].IsVolume ||
					itemList[i].IsFlashAirSystemFolder)
				{
					itemList.Remove(itemList[i]);
					continue;
				}

				if (!itemList[i].IsDirectory)
				{
					if (!itemList[i].IsImageFile)
					{
						itemList.Remove(itemList[i]);
					}
					continue;
				}

				var path = itemList[i].FilePath;
				itemList.Remove(itemList[i]);
				itemList.AddRange(await GetFileListAllAsync(client, path, token, card));
			}
			return itemList;
		}

		/// <summary>
		/// Get a list of files in a specified directory in FlashAir card.
		/// </summary>
		/// <param name="client">HttpClient</param>
		/// <param name="remoteDirectoryPath">Remote directory path</param>
		/// <param name="token">CancellationToken</param>
		/// <param name="card">FlashAir card information</param>
		/// <remarks>This method is part of parent method.</remarks>
		private static async Task<List<FileItemViewModel>> GetFileListEachAsync(HttpClient client, string remoteDirectoryPath, CancellationToken token, CardInfo card)
		{
			var remotePath = ComposeRemotePath(FileManagerCommand.GetFileList, remoteDirectoryPath);

			var items = await DownloadStringAsync(client, remotePath, token, card);

			return items.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries)
				.Select(item => new FileItemViewModel(item, remoteDirectoryPath))
				.Where(x => x.IsImported)
				.ToList();
		}

		/// <summary>
		/// Get a list of files in a specified directory in FlashAir card.
		/// </summary>
		/// <param name="remoteDirectoryPath">Remote directory path</param>
		/// <param name="token">CancellationToken</param>
		/// <param name="card">FlashAir card information</param>
		/// <remarks>This method is not actually used.</remarks>
		internal static async Task<List<FileItemViewModel>> GetFileListAsync(string remoteDirectoryPath, CancellationToken token, CardInfo card)
		{
			if (String.IsNullOrWhiteSpace(remoteDirectoryPath))
				throw new ArgumentNullException("remoteDirectoryPath");

			var remotePath = ComposeRemotePath(FileManagerCommand.GetFileList, remoteDirectoryPath);

			try
			{
				using (var client = new HttpClient() { Timeout = timeoutLength })
				{
					var items = await DownloadStringAsync(client, remotePath, token, card).ConfigureAwait(false);

					return items.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries)
						.Select(item => new FileItemViewModel(item, remoteDirectoryPath))
						.Where(x => x.IsImported)
						.ToList();
				}
			}
			catch
			{
				Debug.WriteLine("Failed to get file list.");
				throw;
			}
		}

		/// <summary>
		/// Get number of files in a specified directory in FlashAir card.
		/// </summary>
		/// <param name="remoteDirectoryPath">Remote directory path</param>
		/// <param name="token">CancellationToken</param>
		/// <param name="card">FlashAir card information</param>
		/// <remarks>This method is not actually used.</remarks>
		internal static async Task<int> GetFileNumAsync(string remoteDirectoryPath, CancellationToken token, CardInfo card)
		{
			if (String.IsNullOrWhiteSpace(remoteDirectoryPath))
				throw new ArgumentNullException("remoteDirectoryPath");

			var remotePath = ComposeRemotePath(FileManagerCommand.GetFileNum, remoteDirectoryPath);

			try
			{
				using (var client = new HttpClient() { Timeout = timeoutLength })
				{
					var itemNum = await DownloadStringAsync(client, remotePath, token, card).ConfigureAwait(false);

					int num;
					return int.TryParse(itemNum, out num) ? num : 0;
				}
			}
			catch
			{
				Debug.WriteLine("Failed to get the number of files.");
				throw;
			}
		}

		/// <summary>
		/// Get a thumbnail of a specified image file in FlashAir card.
		/// </summary>
		/// <param name="remoteFilePath">Remote file path</param>
		/// <param name="token">CancellationToken</param>
		/// <param name="card">FlashAir card information</param>
		internal static async Task<BitmapImage> GetThumbnailAsync(string remoteFilePath, CancellationToken token, CardInfo card)
		{
			if (String.IsNullOrWhiteSpace(remoteFilePath))
				throw new ArgumentNullException("remoteFilePath");

			var remotePath = ComposeRemotePath(FileManagerCommand.GetThumbnail, remoteFilePath);

			try
			{
				using (var client = new HttpClient() { Timeout = timeoutLength })
				{
					var bytes = await DownloadBytesAsync(client, remotePath, token, card).ConfigureAwait(false);

					return await ImageManager.ConvertBytesToBitmapImageAsync(bytes).ConfigureAwait(false);
				}
			}
			catch (ImageNotSupportedException)
			{
				// This exception should not be thrown because thumbnail data is directly provided by FlashAir card.
				return null;
			}
			catch (Exception ex)
			{
				if ((ex.GetType() == typeof(RemoteFileNotFoundException)) ||
					((ex.GetType() == typeof(RemoteConnectionUnableException)) &&
					(((RemoteConnectionUnableException)ex).Code == HttpStatusCode.InternalServerError)))
				{
					// If image file is not JPEG format or if there is no Exif standardized thumbnail stored,
					// StatusCode will be HttpStatusCode.NotFound. Or it may be HttpStatusCode.InternalServerError
					// when image file is non-standard JPEG format.
					Debug.WriteLine("Image file may not be JPEG format or may contain no thumbnail.");
					throw new RemoteFileThumbnailFailedException(remotePath);
				}

				Debug.WriteLine("Failed to get a thumbnail.");
				throw;
			}
		}

		/// <summary>
		/// Get file data of a specified remote file in FlashAir card and save it in local folder. 
		/// </summary>
		/// <param name="remoteFilePath">Remote file path</param>
		/// <param name="localFilePath">Local file path</param>
		/// <param name="size">File size provided by FlashAir card</param>
		/// <param name="itemDate">Date provided by FlashAir card</param>
		/// <param name="canReadExif">Whether can read Exif metadata from the file</param>
		/// <param name="progress">Progress</param>
		/// <param name="token">CancellationToken</param>
		/// <param name="card">FlashAir card information</param>
		/// <returns>Byte array of the file</returns>
		internal static async Task<byte[]> GetSaveFileAsync(string remoteFilePath, string localFilePath, int size, DateTime itemDate, bool canReadExif, IProgress<ProgressInfo> progress, CancellationToken token, CardInfo card)
		{
			if (String.IsNullOrWhiteSpace(remoteFilePath))
				throw new ArgumentNullException("remoteFilePath");

			if (String.IsNullOrWhiteSpace(localFilePath))
				throw new ArgumentNullException("localFilePath");

			var remotePath = ComposeRemotePath(FileManagerCommand.None, remoteFilePath);

			try
			{
				using (var client = new HttpClient() { Timeout = timeoutLength })
				{
					var bytes = await DownloadBytesAsync(client, remotePath, size, progress, token, card).ConfigureAwait(false);

					using (var fs = new FileStream(localFilePath, FileMode.Create, FileAccess.Write))
					{
						await fs.WriteAsync(bytes, 0, bytes.Length, token).ConfigureAwait(false);
					}

					// Conform date of copied file in local folder to that of original file in FlashAir card.
					var localFileInfo = new FileInfo(localFilePath);
					localFileInfo.CreationTime = itemDate; // Creation time
					localFileInfo.LastWriteTime = itemDate; // Last write time

					// Overwrite creation time of copied file by date of image taken from Exif metadata.
					if (canReadExif)
					{
						var exifDateTaken = await ImageManager.GetExifDateTakenAsync(bytes);
						if (exifDateTaken != DateTime.MinValue)
						{
							localFileInfo.CreationTime = exifDateTaken;
						}
					}

					return bytes;
				}
			}
			catch
			{
				Debug.WriteLine("Failed to get and save a file.");
				throw;
			}
		}

		/// <summary>
		/// Delete a specified remote file in FlashAir card.
		/// </summary>
		/// <param name="remoteFilePath">Remote file path</param>
		/// <param name="token">CancellationToken</param>
		internal static async Task DeleteFileAsync(string remoteFilePath, CancellationToken token)
		{
			if (String.IsNullOrWhiteSpace(remoteFilePath))
				throw new ArgumentNullException("remoteFilePath");

			var remotePath = ComposeRemotePath(FileManagerCommand.DeleteFile, remoteFilePath);

			try
			{
				using (var client = new HttpClient() { Timeout = timeoutLength })
				{
					var result = await DownloadStringAsync(client, remotePath, token, null).ConfigureAwait(false);

					// "SUCCESS": If succeeded.
					// "ERROR":   If failed.
					if (!result.Equals("SUCCESS", StringComparison.Ordinal))
						throw new RemoteFileDeletionFailedException(String.Format("Result: {0}", result), remotePath);
				}
			}
			catch (RemoteFileNotFoundException)
			{
				// If upload.cgi is disabled, StatusCode will be HttpStatusCode.NotFound.
				throw new RemoteFileDeletionFailedException(remotePath);
			}
			catch
			{
				Debug.WriteLine("Failed to delete a remote file.");
				throw;
			}
		}

		/// <summary>
		/// Get firmware version of FlashAir card.
		/// </summary>
		/// <param name="token">CancellationToken</param>
		internal static async Task<string> GetFirmwareVersionAsync(CancellationToken token)
		{
			var remotePath = ComposeRemotePath(FileManagerCommand.GetFirmwareVersion, String.Empty);

			try
			{
				using (var client = new HttpClient() { Timeout = timeoutLength })
				{
					return await DownloadStringAsync(client, remotePath, token, null).ConfigureAwait(false);
				}
			}
			catch
			{
				Debug.WriteLine("Failed to get firmware version.");
				throw;
			}
		}

		/// <summary>
		/// Get CID of FlashAir card.
		/// </summary>
		/// <param name="token">CancellationToken</param>
		/// <returns>If succeeded, CID. If failed, empty string.</returns>
		internal static async Task<string> GetCidAsync(CancellationToken token)
		{
			var remotePath = ComposeRemotePath(FileManagerCommand.GetCid, String.Empty);

			try
			{
				using (var client = new HttpClient() { Timeout = timeoutLength })
				{
					return await DownloadStringAsync(client, remotePath, token, null).ConfigureAwait(false);
				}
			}
			catch (RemoteConnectionUnableException)
			{
				return String.Empty;
			}
			catch
			{
				Debug.WriteLine("Failed to get CID.");
				throw;
			}
		}

		/// <summary>
		/// Get SSID of FlashAir card.
		/// </summary>
		/// <param name="token">CancellationToken</param>
		internal static async Task<string> GetSsidAsync(CancellationToken token)
		{
			var remotePath = ComposeRemotePath(FileManagerCommand.GetSsid, String.Empty);

			try
			{
				using (var client = new HttpClient() { Timeout = timeoutLength })
				{
					return await DownloadStringAsync(client, remotePath, token, null).ConfigureAwait(false);
				}
			}
			catch
			{
				Debug.WriteLine("Failed to get SSID.");
				throw;
			}
		}

		/// <summary>
		/// Check update status of FlashAir card.
		/// </summary>
		/// <returns>True if update status is set</returns>
		internal static async Task<bool> CheckUpdateStatusAsync()
		{
			var remotePath = ComposeRemotePath(FileManagerCommand.GetUpdateStatus, String.Empty);

			try
			{
				using (var client = new HttpClient() { Timeout = timeoutLength })
				{
					var status = await DownloadStringAsync(client, remotePath, CancellationToken.None, null).ConfigureAwait(false);

					// 1: If memory has been updated.
					// 0: If not.
					return (status == "1");
				}
			}
			catch
			{
				Debug.WriteLine("Failed to check update status.");
				throw;
			}
		}

		/// <summary>
		/// Get time stamp of write event in FlashAir card.
		/// </summary>
		internal static async Task<int> GetWriteTimeStampAsync()
		{
			return await GetWriteTimeStampAsync(CancellationToken.None);
		}

		/// <summary>
		/// Get time stamp of write event in FlashAir card.
		/// </summary>
		/// <param name="token">CancellationToken</param>
		/// <returns>If succeeded, time stamp (msec). If failed, -1.</returns>
		/// <remarks>If no write event occurred since FlashAir card started running, this value will be 0.</remarks>
		internal static async Task<int> GetWriteTimeStampAsync(CancellationToken token)
		{
			var remotePath = ComposeRemotePath(FileManagerCommand.GetWriteTimeStamp, String.Empty);

			try
			{
				using (var client = new HttpClient() { Timeout = timeoutLength })
				{
					var timeStamp = await DownloadStringAsync(client, remotePath, token, null).ConfigureAwait(false);

					int num;
					return int.TryParse(timeStamp, out num) ? num : -1;
				}
			}
			catch (RemoteConnectionUnableException)
			{
				// If request for time stamp of write event is not supported, StatusCode will be HttpStatusCode.BadRequest.
				return -1;
			}
			catch
			{
				Debug.WriteLine("Failed to get time stamp of write event.");
				throw;
			}
		}

		/// <summary>
		/// Get Upload parameters of FlashAir card.
		/// </summary>
		/// <param name="token">CancellationToken</param>
		/// <returns>If succeeded, Upload parameters (string). If failed, empty string.</returns>
		internal static async Task<string> GetUploadAsync(CancellationToken token)
		{
			var remotePath = ComposeRemotePath(FileManagerCommand.GetUpload, String.Empty);

			try
			{
				using (var client = new HttpClient() { Timeout = timeoutLength })
				{
					return await DownloadStringAsync(client, remotePath, token, null).ConfigureAwait(false);
				}
			}
			catch (RemoteConnectionUnableException)
			{
				// If request for Upload parameters is not supported, StatusCode will be HttpStatusCode.BadRequest.
				return String.Empty;
			}
			catch
			{
				Debug.WriteLine("Failed to get Upload parameters.");
				throw;
			}
		}

		#endregion


		#region Method (Private)

		private static async Task<string> DownloadStringAsync(HttpClient client, string path, CancellationToken token, CardInfo card)
		{
			var bytes = await DownloadBytesAsync(client, path, 0, null, token, card);

			if (recordsDownloadString)
				await RecordDownloadStringAsync(path, bytes);

			// Response from FlashAir card seems to be ASCII encoded. Not certain though.
			return Encoding.ASCII.GetString(bytes);
		}

		private static async Task<byte[]> DownloadBytesAsync(HttpClient client, string path, CancellationToken token, CardInfo card)
		{
			return await DownloadBytesAsync(client, path, 0, null, token, card);
		}

		private static async Task<byte[]> DownloadBytesAsync(HttpClient client, string path, int size, CancellationToken token, CardInfo card)
		{
			return await DownloadBytesAsync(client, path, size, null, token, card);
		}

		private static async Task<byte[]> DownloadBytesAsync(HttpClient client, string path, int size, IProgress<ProgressInfo> progress, CancellationToken token, CardInfo card)
		{
			int retryCount = 0;

			while (true)
			{
				retryCount++;

				try
				{
					try
					{
						using (var response = await client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, token))
						{
							// If HttpResponseMessage.EnsureSuccessStatusCode is set, an exception by this setting
							// will be thrown in the scope of HttpClient and so cannot be caught in this method.
							switch (response.StatusCode)
							{
								case HttpStatusCode.OK:
									// None.
									break;
								case HttpStatusCode.Unauthorized:
								case HttpStatusCode.InternalServerError:
								case HttpStatusCode.BadRequest:
									throw new RemoteConnectionUnableException(response.StatusCode);
								case HttpStatusCode.NotFound:
									// This exception does not always mean that the specified file is missing.
									throw new RemoteFileNotFoundException("File is missing or request cannot be handled!", path);
								default:
									throw new HttpRequestException(String.Format("StatusCode: {0}", response.StatusCode));
							}

							if ((0 < size) &&
								(!response.Content.Headers.ContentLength.HasValue ||
								 (response.Content.Headers.ContentLength.Value != size)))
								throw new RemoteFileInvalidException("Data length does not match!", path);

							// Because of HttpCompletionOption.ResponseHeadersRead option, neither CancellationToken
							// nor HttpClient.Timeout setting works for response content.

							// Register delegate to CancellationToken because CancellationToken can no longer
							// directly affect HttpClient. Disposing the HttpResponseMessage will make ReadAsStreamAsync
							// method throw an ObjectDisposedException and so exit this operation.
							var ctr = new CancellationTokenRegistration();
							try
							{
								ctr = token.Register(() => response.Dispose());
							}
							catch (ObjectDisposedException ode)
							{
								// If CancellationTokenSource has been disposed during operation (it unlikely happens),
								// this exception will be thrown.
								Debug.WriteLine("CancellationTokenSource has been disposed when tried to register delegate. {0}", ode);
							}
							using (ctr)
							{
								var tcs = new TaskCompletionSource<bool>();

								// Start timer to monitor network connection.
								using (var monitorTimer = new Timer(async s =>
								{
									if (!await NetworkChecker.IsNetworkConnectedAsync(card))
									{
										((TaskCompletionSource<bool>)s).TrySetResult(true);
									}
								}, tcs, monitorLength, monitorLength))
								{
									var monitorTask = tcs.Task;

									if ((size == 0) || (progress == null))
									{
										// Route without progress reporting
										var readTask = Task.Run(async () => await response.Content.ReadAsByteArrayAsync());
										var timeoutTask = Task.Delay(timeoutLength);

										var completedTask = await Task.WhenAny(readTask, timeoutTask, monitorTask);
										if (completedTask == timeoutTask)
											throw new TimeoutException("Reading response content timed out!");
										if (completedTask == monitorTask)
											throw new RemoteConnectionLostException("Connection lost!");

										var bytes = await readTask;

										if ((0 < size) && (bytes.Length != size))
											throw new RemoteFileInvalidException("Data length does not match!", path);

										return bytes;
									}
									else
									{
										// Route with progress reporting
										int readLength;
										int readLengthTotal = 0;

										var buffer = new byte[65536]; // 64KiB
										var bufferTotal = new byte[size];

										const double stepUint = 524288D; // 512KiB
										double stepTotal = Math.Ceiling(size / stepUint); // Number of steps to report during downloading
										if (stepTotal < 6)
											stepTotal = 6; // Minimum number of steps

										double stepCurrent = 1D;
										var startTime = DateTime.Now;

										using (var stream = await response.Content.ReadAsStreamAsync())
										{
											while (readLengthTotal != size)
											{
												// CancellationToken in overload of ReadAsync method will not work for response content.
												var readTask = Task.Run(async () => await stream.ReadAsync(buffer, 0, buffer.Length));
												var timeoutTask = Task.Delay(timeoutLength);

												var completedTask = await Task.WhenAny(readTask, timeoutTask, monitorTask);
												if (completedTask == timeoutTask)
													throw new TimeoutException("Reading response content timed out!");
												if (completedTask == monitorTask)
													throw new RemoteConnectionLostException("Connection lost!");

												readLength = await readTask;

												if ((readLength == 0) || (readLengthTotal + readLength > size))
													throw new RemoteFileInvalidException("Data length does not match!", path);

												Buffer.BlockCopy(buffer, 0, bufferTotal, readLengthTotal, readLength);

												readLengthTotal += readLength;

												monitorTimer.Change(monitorLength, monitorLength);

												// Report if read length in total exceeds stepped length.
												if (stepCurrent / stepTotal * size <= readLengthTotal)
												{
													stepCurrent++;
													progress.Report(new ProgressInfo(
														currentValue: readLengthTotal,
														totalValue: size,
														elapsedTime: DateTime.Now - startTime));
												}
											}
										}
										return bufferTotal;
									}
								}
							}
						}
					}
					// Sort out exceptions.					
					catch (OperationCanceledException) // Including TaskCanceledException
					{
						if (!token.IsCancellationRequested)
							// If cancellation has not been requested, the reason of this exception must be timeout.
							// This is for response header only.
							throw new TimeoutException("Reading response header timed out!");

						throw;
					}
					catch (ObjectDisposedException)
					{
						if (token.IsCancellationRequested)
							// If cancellation has been requested, the reason of this exception must be cancellation.
							// This is for response content only.
							throw new OperationCanceledException();

						throw;
					}
					catch (IOException ie)
					{
						var inner = ie.InnerException;
						if ((inner != null) && (inner.GetType() == typeof(WebException)) &&
							(((WebException)inner).Status == WebExceptionStatus.ConnectionClosed) &&
							token.IsCancellationRequested)
							// If cancellation has been requested during downloading, this exception may be thrown.
							throw new OperationCanceledException();

						throw;
					}
					catch (HttpRequestException hre)
					{
						var inner = hre.InnerException;
						if ((inner != null) && (inner.GetType() == typeof(WebException)))
							// If unable to connect to FlashAir card, this exception will be thrown.
							// The Status may vary, such as WebExceptionStatus.NameResolutionFailure,
							// WebExceptionStatus.ConnectFailure.
							throw new RemoteConnectionUnableException(((WebException)hre.InnerException).Status);

						throw;
					}
				}
				catch (RemoteConnectionUnableException)
				{
					if (retryCount >= retryCountMax)
						throw;
				}
				catch (Exception ex)
				{
					Debug.WriteLine("Failed to download byte array. {0}", ex);
					throw;
				}

				// Wait interval before retry.
				if (TimeSpan.Zero < retryLength)
					await Task.Delay(retryLength, token);
			}
		}

		#endregion


		#region Helper

		private static readonly Dictionary<FileManagerCommand, string> commandMap =
			new Dictionary<FileManagerCommand, string>()
			{
				{FileManagerCommand.None, String.Empty},
				{FileManagerCommand.GetFileList, @"command.cgi?op=100&DIR=/"},
				{FileManagerCommand.GetFileNum, @"command.cgi?op=101&DIR=/"},
				{FileManagerCommand.GetThumbnail, @"thumbnail.cgi?/"},
				{FileManagerCommand.GetFirmwareVersion, @"command.cgi?op=108"},
				{FileManagerCommand.GetCid, @"command.cgi?op=120"},
				{FileManagerCommand.GetSsid, @"command.cgi?op=104"},
				{FileManagerCommand.GetUpdateStatus, @"command.cgi?op=102"},
				{FileManagerCommand.GetWriteTimeStamp, @"command.cgi?op=121"},
				{FileManagerCommand.GetUpload, @"command.cgi?op=118"},
				{FileManagerCommand.DeleteFile, @"upload.cgi?DEL=/"},
			};

		/// <summary>
		/// Compose path to remote file in FlashAir card inserting CGI command string.
		/// </summary>
		/// <param name="command">CGI command type</param>
		/// <param name="remotePath">Remote path</param>
		private static string ComposeRemotePath(FileManagerCommand command, string remotePath)
		{
			return Settings.Current.RemoteRoot + commandMap[command] + remotePath.TrimStart('/');
		}

		private static readonly bool recordsDownloadString = Debugger.IsAttached || CommandLine.RecordsDownloadLog;

		/// <summary>
		/// Record result of DownloadStringAsync method for debugging purpose.
		/// </summary>
		/// <param name="requestPath">Request path</param>
		/// <param name="responseBytes">Response byte array</param>
		private static async Task RecordDownloadStringAsync(string requestPath, byte[] responseBytes)
		{
			var filePath = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
				System.Reflection.Assembly.GetExecutingAssembly().GetName().Name,
				"download.log");

			try
			{
				if (File.Exists(filePath) &&
					(File.GetLastWriteTime(filePath) < DateTime.Now.AddHours(-1)))
					File.Delete(filePath);

				var result = String.Format("[{0:HH:mm:ss fff}]", DateTime.Now) + Environment.NewLine
					+ "request => " + requestPath + Environment.NewLine
					+ "response -> " + Environment.NewLine
					+ Encoding.ASCII.GetString(responseBytes) + Environment.NewLine;

				using (var sw = new StreamWriter(filePath, true, Encoding.UTF8))
				{
					await sw.WriteAsync(result);
				}
			}
			catch (Exception ex)
			{
				Trace.WriteLine(String.Format("Failed to record download log to AppData. {0}", ex));
			}
		}

		#endregion
	}
}