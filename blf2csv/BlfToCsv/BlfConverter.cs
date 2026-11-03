using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace BlfToCsv;

public static class BlfConverter
{
    private const uint LogContainer = 10;
    private const uint LinMessage = 11;
    private const uint LinMessage2 = 57;
    private const uint CanFdMessage = 100;

    private const uint ExtendedCanIdFlag = 0x80000000;
    private const uint ExtendedCanIdMask = 0x1FFFFFFF;
    private const byte TransmitFlag = 0x01;
    private const byte RemoteFrameFlag = 0x80;
    private const byte ExtendedDataLengthFlag = 0x01;

    public static int ConvertFile(string blfPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blfPath);
        if (!File.Exists(blfPath))
            throw new FileNotFoundException("找不到指定的 BLF 檔案。", blfPath);

        string csvPath = Path.ChangeExtension(blfPath, ".csv");
        string tempPath = csvPath + ".tmp";

        try
        {
            int sequence = ConvertToTemporaryFile(blfPath, tempPath);
            File.Move(tempPath, csvPath, true);
            return sequence;
        }
        catch
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
            throw;
        }
    }

    private static int ConvertToTemporaryFile(string blfPath, string tempPath)
    {
        using var input = new FileStream(blfPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        ValidateFileHeader(input);

        using var output = new StreamWriter(tempPath, false, new UTF8Encoding(true))
        {
            NewLine = "\n"
        };
        OutputFormat? outputFormat = null;
        decimal? previousCanTimestamp = null;
        int sequence = 0;
        byte[] tail = [];

        foreach (byte[] block in ReadLogContainers(input))
        {
            byte[] data = Merge(tail, block);
            int position = 0;

            while (position + 16 <= data.Length)
            {
                if (!data.AsSpan(position, 4).SequenceEqual("LOBJ"u8))
                    throw new InvalidDataException($"BLF 物件格式錯誤，位移 0x{position:X}。");

                ushort headerSize = ReadUInt16(data, position + 4);
                uint objectSizeValue = ReadUInt32(data, position + 8);
                uint objectType = ReadUInt32(data, position + 12);
                if (objectSizeValue < headerSize || objectSizeValue > int.MaxValue)
                    throw new InvalidDataException("BLF 物件長度不正確。");

                int objectSize = (int)objectSizeValue;
                int paddedSize = checked(objectSize + (objectSize % 4));
                if (position + paddedSize > data.Length)
                    break;

                ReadOnlySpan<byte> obj = data.AsSpan(position, objectSize);
                BusFrame? frame = objectType switch
                {
                    LinMessage => ParseLegacyLinMessage(obj, headerSize),
                    LinMessage2 => ParseLinMessage2(obj, headerSize),
                    CanFdMessage => ParseCanFdMessage(obj, headerSize),
                    _ => null
                };

                if (frame is LinFrame { Data.Length: > 0 } linFrame)
                {
                    EnsureOutputFormat(output, ref outputFormat, OutputFormat.Lin);
                    sequence++;
                    WriteLinCsvRow(output, sequence, linFrame);
                }
                else if (frame is CanFrame canFrame)
                {
                    EnsureOutputFormat(output, ref outputFormat, OutputFormat.Can);
                    decimal interval = previousCanTimestamp is null
                        ? 0m
                        : canFrame.Seconds - previousCanTimestamp.Value;
                    if (interval < 0)
                        throw new InvalidDataException("CAN 訊框時間戳記順序錯誤。");

                    previousCanTimestamp = canFrame.Seconds;
                    sequence++;
                    WriteCanCsvRow(output, sequence, canFrame, interval);
                }

                position += paddedSize;
            }

            tail = data[position..];
        }

        if (tail.Length != 0 && !tail.All(static value => value == 0))
            throw new InvalidDataException("BLF 檔案結尾包含未完成的物件。");
        if (sequence == 0)
            throw new InvalidDataException("BLF 中沒有可轉換的 LIN、CAN 或 CAN FD 訊框。");

        return sequence;
    }

    private static void EnsureOutputFormat(
        TextWriter writer,
        ref OutputFormat? currentFormat,
        OutputFormat requiredFormat)
    {
        if (currentFormat is null)
        {
            currentFormat = requiredFormat;
            if (requiredFormat == OutputFormat.Can)
            {
                writer.WriteLine("USB2CANFD&LIN Display List File");
                writer.WriteLine("序号,帧ID(Hex),长度,数据(Hex),时间标识,方向,帧类型,帧格式,CAN类型,通道号,设备号");
            }
            else
            {
                writer.WriteLine("USB2CAN&LIN Display List File V2.0");
                writer.WriteLine("序号,ID[PID],数据(Hex),校验(Hex),校验模式,数据类型,时间标识,通道号,事件");
            }
            return;
        }

        if (currentFormat != requiredFormat)
            throw new InvalidDataException("同一個 BLF 同時包含 CAN 與 LIN 訊框，無法寫入單一格式的 CSV。");
    }

    private static byte[] Merge(byte[] tail, byte[] block)
    {
        if (tail.Length == 0)
            return block;

        byte[] result = new byte[tail.Length + block.Length];
        Buffer.BlockCopy(tail, 0, result, 0, tail.Length);
        Buffer.BlockCopy(block, 0, result, tail.Length, block.Length);
        return result;
    }

    private static void ValidateFileHeader(Stream input)
    {
        Span<byte> header = stackalloc byte[8];
        input.ReadExactly(header);
        if (!header[..4].SequenceEqual("LOGG"u8))
            throw new InvalidDataException("不是有效的 Vector BLF 檔案。");

        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        if (headerSize < 8 || headerSize > input.Length)
            throw new InvalidDataException("BLF 檔頭長度不正確。");
        input.Position = headerSize;
    }

    private static IEnumerable<byte[]> ReadLogContainers(Stream input)
    {
        byte[] baseHeader = new byte[16];
        while (input.Position < input.Length)
        {
            int first = input.Read(baseHeader, 0, baseHeader.Length);
            if (first == 0)
                yield break;
            if (first != baseHeader.Length)
                throw new EndOfStreamException("BLF 容器檔頭不完整。");
            if (!baseHeader.AsSpan(0, 4).SequenceEqual("LOBJ"u8))
                throw new InvalidDataException("BLF 容器簽章錯誤。");

            uint objectSizeValue = ReadUInt32(baseHeader, 8);
            uint objectType = ReadUInt32(baseHeader, 12);
            if (objectType != LogContainer || objectSizeValue < 32 || objectSizeValue > int.MaxValue)
                throw new InvalidDataException("BLF 外層物件不是有效的記錄容器。");

            int objectSize = (int)objectSizeValue;
            byte[] body = new byte[objectSize - 16];
            input.ReadExactly(body);
            int padding = objectSize % 4;
            if (padding > 0)
            {
                if (input.Position + padding > input.Length)
                    throw new EndOfStreamException("BLF 容器填充資料不完整。");
                input.Position += padding;
            }

            ushort compressionMethod = ReadUInt16(body, 0);
            uint uncompressedSizeValue = ReadUInt32(body, 8);
            if (uncompressedSizeValue > int.MaxValue)
                throw new InvalidDataException("BLF 解壓縮資料過大。");

            byte[] compressed = body[16..];
            if (compressionMethod == 0)
            {
                if (compressed.Length != uncompressedSizeValue)
                    throw new InvalidDataException("BLF 未壓縮容器長度不正確。");
                yield return compressed;
            }
            else if (compressionMethod == 2)
            {
                using var source = new MemoryStream(compressed, false);
                using var zlib = new ZLibStream(source, CompressionMode.Decompress);
                byte[] result = new byte[(int)uncompressedSizeValue];
                zlib.ReadExactly(result);
                if (zlib.ReadByte() != -1)
                    throw new InvalidDataException("BLF 解壓縮資料超過宣告長度。");
                yield return result;
            }
            else
            {
                throw new InvalidDataException($"不支援的 BLF 壓縮方法：{compressionMethod}。");
            }
        }
    }

    private static CanFrame ParseCanFdMessage(ReadOnlySpan<byte> obj, int headerSize)
    {
        const int minimumBodySize = 88;
        if (headerSize < 32 || obj.Length < headerSize + minimumBodySize)
            throw new InvalidDataException("CAN_FD_MESSAGE 物件長度不足。");

        ReadOnlySpan<byte> body = obj[headerSize..];
        uint timestampFlags = BinaryPrimitives.ReadUInt32LittleEndian(obj[16..]);
        ulong timestamp = BinaryPrimitives.ReadUInt64LittleEndian(obj[24..]);
        ushort channel = BinaryPrimitives.ReadUInt16LittleEndian(body);
        byte messageFlags = body[2];
        uint rawId = BinaryPrimitives.ReadUInt32LittleEndian(body[4..]);
        byte canFdFlags = body[13];
        int validDataBytes = body[14];
        if (validDataBytes > 64)
            throw new InvalidDataException($"CAN 資料長度超過 64 Byte：{validDataBytes}。");

        byte[] payload = body.Slice(20, validDataBytes).ToArray();
        bool isExtended = (rawId & ExtendedCanIdFlag) != 0;
        uint id = rawId & ExtendedCanIdMask;
        if (!isExtended && id > 0x7FF)
            throw new InvalidDataException($"標準 CAN ID 超過 11-bit 範圍：0x{id:X8}。");

        return new CanFrame(
            ToSeconds(timestamp, timestampFlags),
            channel,
            id,
            payload,
            (messageFlags & TransmitFlag) != 0,
            isExtended,
            (messageFlags & RemoteFrameFlag) != 0,
            (canFdFlags & ExtendedDataLengthFlag) != 0);
    }

    private static LinFrame ParseLinMessage2(ReadOnlySpan<byte> obj, int headerSize)
    {
        const int minimumBodySize = 124;
        if (headerSize < 32 || obj.Length < headerSize + minimumBodySize)
            throw new InvalidDataException("LIN_MESSAGE2 物件長度不足。");

        ReadOnlySpan<byte> body = obj[headerSize..];
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(obj[16..]);
        ulong timestamp = BinaryPrimitives.ReadUInt64LittleEndian(obj[24..]);
        ushort channel = BinaryPrimitives.ReadUInt16LittleEndian(body[12..]);
        byte protectedId = body[37];
        byte id = (byte)(protectedId & 0x3F);
        byte dlc = (byte)Math.Min((int)body[38], 8);
        byte checksumModel = body[39];
        byte[] payload = body.Slice(112, dlc).ToArray();
        byte checksum = CalculateChecksum(protectedId, payload, checksumModel != 0);
        byte direction = body[122];
        return new LinFrame(ToSeconds(timestamp, flags), channel, id, payload, checksum, checksumModel, direction);
    }

    private static LinFrame ParseLegacyLinMessage(ReadOnlySpan<byte> obj, int headerSize)
    {
        const int minimumBodySize = 20;
        if (headerSize < 32 || obj.Length < headerSize + minimumBodySize)
            throw new InvalidDataException("LIN_MESSAGE 物件長度不足。");

        ReadOnlySpan<byte> body = obj[headerSize..];
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(obj[16..]);
        ulong timestamp = BinaryPrimitives.ReadUInt64LittleEndian(obj[24..]);
        ushort channel = BinaryPrimitives.ReadUInt16LittleEndian(body);
        byte id = body[2];
        byte dlc = (byte)Math.Min((int)body[3], 8);
        byte[] payload = body.Slice(4, dlc).ToArray();
        byte recordedChecksum = (byte)BinaryPrimitives.ReadUInt16LittleEndian(body[16..]);
        byte direction = body[18];
        byte checksumModel = IsEnhancedChecksum(id, payload, recordedChecksum) ? (byte)1 : (byte)0;
        byte checksum = CalculateChecksum(CalculateProtectedId(id), payload, checksumModel != 0);
        return new LinFrame(ToSeconds(timestamp, flags), channel, id, payload, checksum, checksumModel, direction);
    }

    private static void WriteCanCsvRow(TextWriter writer, int sequence, CanFrame frame, decimal interval)
    {
        string data = FormatHexData(frame.Data);
        string direction = frame.IsTransmit ? "发送" : "接收";
        string frameType = frame.IsExtended ? "扩展帧" : "标准帧";
        string frameFormat = frame.IsRemote ? "远程帧" : "数据帧";
        string canType = frame.IsCanFd ? "CANFD" : "CAN";
        writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{sequence},0x{frame.Id:X8},{frame.Data.Length},{data},{interval:F6},{direction},{frameType},{frameFormat},{canType},CAN{frame.Channel},0xFFFFFF00"));
    }

    private static void WriteLinCsvRow(TextWriter writer, int sequence, LinFrame frame)
    {
        byte pid = CalculateProtectedId(frame.Id);
        string data = FormatHexData(frame.Data);
        string checksumMode = frame.ChecksumModel == 0 ? "经典" : "增强";
        string dataType = frame.Direction == 0 ? "主机读" : "主机写";
        writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{sequence},{frame.Id:X2} [{pid:X2}],{data},{frame.Checksum:X2},{checksumMode},{dataType},{frame.Seconds:F3},LIN{frame.Channel},"));
    }

    private static string FormatHexData(byte[] data) =>
        string.Join(' ', data.Select(static value => value.ToString("X2", CultureInfo.InvariantCulture)));

    private static decimal ToSeconds(ulong timestamp, uint flags) =>
        (flags & 1) != 0 ? timestamp / 100_000m : timestamp / 1_000_000_000m;

    private static byte CalculateProtectedId(byte id)
    {
        id &= 0x3F;
        int p0 = ((id >> 0) ^ (id >> 1) ^ (id >> 2) ^ (id >> 4)) & 1;
        int p1 = (~((id >> 1) ^ (id >> 3) ^ (id >> 4) ^ (id >> 5))) & 1;
        return (byte)(id | (p0 << 6) | (p1 << 7));
    }

    private static bool IsEnhancedChecksum(byte id, byte[] data, byte expected)
    {
        if (id is 0x3C or 0x3D)
            return false;
        return CalculateChecksum(CalculateProtectedId(id), data, true) == expected;
    }

    private static byte CalculateChecksum(byte protectedId, byte[] data, bool enhanced)
    {
        int sum = enhanced ? protectedId : 0;
        foreach (byte value in data)
        {
            sum += value;
            if (sum > byte.MaxValue)
                sum -= byte.MaxValue;
        }
        return (byte)~sum;
    }

    private static ushort ReadUInt16(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));

    private static uint ReadUInt32(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));

    private enum OutputFormat
    {
        Lin,
        Can
    }

    private abstract record BusFrame(decimal Seconds);

    private sealed record CanFrame(
        decimal Seconds,
        ushort Channel,
        uint Id,
        byte[] Data,
        bool IsTransmit,
        bool IsExtended,
        bool IsRemote,
        bool IsCanFd) : BusFrame(Seconds);

    private sealed record LinFrame(
        decimal Seconds,
        ushort Channel,
        byte Id,
        byte[] Data,
        byte Checksum,
        byte ChecksumModel,
        byte Direction) : BusFrame(Seconds);
}
