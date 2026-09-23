using System;

namespace Ryujinx.Horizon.Common
{
    public class InvalidResultException : Exception
    {
        public Result? ResultCode { get; }

        public InvalidResultException()
        {
        }

        public InvalidResultException(Result result) : base($"Unexpected result code {result} returned.")
        {
            ResultCode = result;
        }

        public InvalidResultException(string message) : base(message)
        {
        }

        public InvalidResultException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
