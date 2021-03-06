﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SnowyImageCopy.Models
{
	/// <summary>
	/// FlashAir card information
	/// </summary>
	internal class CardInfo
	{
		/// <summary>
		/// Whether FlashAir card is changed
		/// </summary>
		/// <returns>
		/// True:  If changed.
		/// False: If not changed.
		/// Null:  If cannot detect change.
		/// </returns>
		/// <remarks>Checking not only CID but also firmware version and SSID is for the case that firmware version 
		/// of a card is too old to support request for CID. However, if cards have the same firmware version and 
		/// if the firmware version is too old and if cards have the same SSID, change of cards cannot be detected. 
		/// Prior to draw value out of this property, getting the three parameters in sequence is required.</remarks>
		public bool? IsChanged
		{
			get
			{
				if (isFirmwareVersionChanged || isSsidChanged)
					return true;

				return CanGetCid ? isCidChanged : (bool?)null;
			}
		}


		#region Firmware version

		/// <summary>
		/// Firmware version
		/// </summary>
		public string FirmwareVersion
		{
			get { return _firmwareVersion; }
			set
			{
				isFirmwareVersionChanged = (_firmwareVersion != value);
				if (!isFirmwareVersionChanged)
					return;

				_firmwareVersion = value;

				var versionNumber = FindVersionNumber(value);

				isFirmwareVersion103OrNewer = (versionNumber >= new Version(1, 0, 3)); // If versionNumber == null, false.
				isFirmwareVersion202OrNewer = (versionNumber >= new Version(2, 0, 2)); // If versionNumber == null, false.
			}
		}
		private string _firmwareVersion;
		private bool isFirmwareVersionChanged;

		private bool isFirmwareVersion103OrNewer; // Equal to or newer than 1.00.03
		private bool isFirmwareVersion202OrNewer; // Equal to or newer than 2.00.02

		#endregion


		#region CID/SSID

		public bool CanGetCid
		{
			get { return isFirmwareVersion103OrNewer; }
		}

		/// <summary>
		/// CID
		/// </summary>
		public string Cid
		{
			get { return _cid; }
			set
			{
				isCidChanged = (_cid != value);
				if (!isCidChanged)
					return;

				_cid = value;
			}
		}
		private string _cid;
		private bool isCidChanged;

		/// <summary>
		/// SSID
		/// </summary>
		public string Ssid
		{
			get { return _ssid; }
			set
			{
				isSsidChanged = (_ssid != value);
				if (!isSsidChanged)
					return;

				_ssid = value;
			}
		}
		private string _ssid;
		private bool isSsidChanged;

		/// <summary>
		/// Whether PC is connected to FlashAir card by a wireless connection
		/// </summary>
		/// <remarks>
		/// True: Access Point mode, Internet Pass-Thru mode
		/// False: Station mode
		/// </remarks>
		public bool IsWirelessConnected { get; set; }

		#endregion


		#region Time stamp of write event

		public bool CanGetWriteTimeStamp
		{
			get { return isFirmwareVersion202OrNewer; }
		}

		/// <summary>
		/// Time stamp of write event
		/// </summary>
		public int WriteTimeStamp { get; set; }

		#endregion


		#region Thumbnail

		public string ThumbnailFailedPath
		{
			set
			{
				if (_thumbnailFailedPathes == null)
					_thumbnailFailedPathes = new List<string>();

				if (!_thumbnailFailedPathes.Contains(value))
					_thumbnailFailedPathes.Add(value);
			}
		}

		public IReadOnlyList<string> ThumbnailFailedPathes
		{
			get { return _thumbnailFailedPathes ?? (_thumbnailFailedPathes = new List<string>()); }
		}
		private List<string> _thumbnailFailedPathes;

		private const int thumbnailFailedPathesCountMax = 3;

		public bool CanGetThumbnail
		{
			get { return ((_thumbnailFailedPathes == null) || (_thumbnailFailedPathes.Count < thumbnailFailedPathesCountMax)); }
		}

		#endregion


		#region Upload

		public bool CanGetUpload
		{
			get { return isFirmwareVersion202OrNewer; }
		}

		/// <summary>
		/// Upload parameters
		/// </summary>
		public string Upload { get; set; }

		/// <summary>
		/// Whether upload.cgi is disabled
		/// </summary>
		/// <remarks>False will not always mean upload.cgi is enabled. Because request for Upload parameters is 
		/// supported only by newer firmware version, there is no direct way to confirm it.</remarks>
		public bool IsUploadDisabled
		{
			get
			{
				if (!CanGetUpload || String.IsNullOrWhiteSpace(Upload))
					return false;

				// 1:     Uploading is enabled.
				// Other: Uploading is disabled.
				return (Upload != "1");
			}
		}

		#endregion


		#region Helper

		private static readonly Regex versionPattern = new Regex(@"[1-9]\.\d{2}\.\d{2}$", RegexOptions.Compiled);

		private static Version FindVersionNumber(string source)
		{
			if (String.IsNullOrWhiteSpace(source))
				return null;

			var match = versionPattern.Match(source);
			if (!match.Success)
				return null;

			return new Version(match.Value);
		}

		#endregion
	}
}