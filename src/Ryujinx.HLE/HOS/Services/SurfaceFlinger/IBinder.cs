using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Kernel.Threading;
using System;
using System.Runtime.CompilerServices;

namespace Ryujinx.HLE.HOS.Services.SurfaceFlinger
{
    interface IBinder
    {
        ResultCode AdjustRefcount(int addVal, int type);

        void GetNativeHandle(uint typeId, out KReadableEvent readableEvent);

        ResultCode OnTransact(uint code, uint flags, ReadOnlySpan<byte> inputParcel, Span<byte> outputParcel)
        {
            if (outputParcel.Length < Unsafe.SizeOf<ParcelHeader>())
            {
                Logger.Error?.Print(LogClass.SurfaceFlinger,
                    $"Rejected Binder transaction {code}: reply buffer is too small ({outputParcel.Length} bytes).");
                return ResultCode.NotAllocated;
            }

            try
            {
                using Parcel inputParcelReader = new(inputParcel);

                // TODO: support objects?
                using Parcel outputParcelWriter = new((uint)(outputParcel.Length - Unsafe.SizeOf<ParcelHeader>()), 0);

                string inputInterfaceToken = inputParcelReader.ReadInterfaceToken();

                if (!InterfaceToken.Equals(inputInterfaceToken))
                {
                    Logger.Error?.Print(LogClass.SurfaceFlinger,
                        $"Rejected Binder transaction {code}: invalid interface token '{inputInterfaceToken}' (expected '{InterfaceToken}').");
                    return ResultCode.NotAllocated;
                }

                OnTransact(code, flags, inputParcelReader, outputParcelWriter);

                outputParcelWriter.Finish().CopyTo(outputParcel);

                return ResultCode.Success;
            }
            catch (ArgumentException exception)
            {
                Logger.Error?.Print(LogClass.SurfaceFlinger,
                    $"Rejected malformed Binder transaction {code} (flags=0x{flags:X}, input={inputParcel.Length}, output={outputParcel.Length}): {exception.Message}");
                return ResultCode.NotAllocated;
            }
            catch (OverflowException exception)
            {
                Logger.Error?.Print(LogClass.SurfaceFlinger,
                    $"Rejected overflowing Binder transaction {code} (flags=0x{flags:X}, input={inputParcel.Length}, output={outputParcel.Length}): {exception.Message}");
                return ResultCode.NotAllocated;
            }

        }

        void OnTransact(uint code, uint flags, Parcel inputParcel, Parcel outputParcel);

        string InterfaceToken { get; }
    }
}
