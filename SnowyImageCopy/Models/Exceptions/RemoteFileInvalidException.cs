﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;

namespace SnowyImageCopy.Models.Exceptions
{
	/// <summary>
	/// Exception when a remote file in FlashAir card is found invalid
	/// </summary>
	[Serializable]
	internal class RemoteFileInvalidException : Exception
	{
		public string FilePath { get; private set; }


		#region Constructor

		public RemoteFileInvalidException() { }
		public RemoteFileInvalidException(string message) : base(message) { }
		public RemoteFileInvalidException(string message, Exception inner) : base(message, inner) { }
		protected RemoteFileInvalidException(SerializationInfo info, StreamingContext context) : base(info, context) { }

		public RemoteFileInvalidException(string message, string filePath)
			: base(message)
		{
			this.FilePath = filePath;
		}

		#endregion
	}
}