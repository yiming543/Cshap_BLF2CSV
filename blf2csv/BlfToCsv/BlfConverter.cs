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

    public static int ConvertFile(string blfPath)
    {
        string csvPath = Path.ChangeExtension(blfPath, ".csv");
        string tempPath = csvPath + ".tmp";

        try
        {
            using var input = new FileStream(blfPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            ValidateFileHeader(input);
            using var output = new StreamWriter(tempPath, false, new UTF8Encoding(true));
            output.WriteLine("USB2CAN&LIN Display List File V2.0");
            output.WriteLine("序号,ID[PID],数据(Hex),校验(Hex),校验模式,数据类型,时间标识,通道号,事件");

            int sequence = 0;
            byte[] tail = [];
            foreach (byte[] block in ReadLogContainers(input))
            {
                byte[] data;
                if (tail.Length == 0)
                {
                    data = block;
                }
                else
                {
                    data = new byte[tail.Length + block.Length];
                    Buffer.BlockCopy(tail, 0, data, 0, tail.Length);
                    Buffer.BlockCopy(block, 0, data, tail.Length, block.Length);
                }

                int position = 0;
                while (position + 16 <= data.Length)
                {
                    if (!data.AsSpan(position, 4).SequenceEqual("LOBJ"u8))
                        throw new InvalidDataException($"BLF 物件格式錯誤，位移 0x{position:X}。 ");

                    ushort headerSize = ReadUInt16(data, position + 4);
                    uint objectSizeValue = ReadUInt32(data, position + 8);
                    uint objectType = ReadUInt32(data, position + 12);
                    if (objectSizeValue < headerSize || objectSizeValue > int.MaxValue)
                        throw new InvalidDataException("BLF 物件長度不正確。 ");

                    int objectSize = (int)objectSizeValue;
                    int paddedSize = objectSize + (objectSize % 4);
                    if (position + paddedSize > data.Length)
                        break;

                    ReadOnlySpan<byte> obj = data.AsSpan(position, objectSize);
                    LinFrame? frame = objectType switch
                    {
                        LinMessage => ParseLegacyLinMessage(obj, headerSize),
                        LinMessage2 => ParseLinMessage2(obj, headerSize),
                        _ => null
                    };

                    // BLF 也會記錄只有 Header、尚未收到 Response 的 LIN 事件；
                    // 範例 CSV 僅列出含有效資料的完整訊框。
                    if (frame is not null && frame.Data.Length > 0)
                    {
                        sequence++;
                        WriteCsvRow(output, sequence, frame);
                    }

                    position += paddedSize;
                }

                tail = data[position..];
            }

            if (tail.Length != 0 && !tail.All(b => b == 0))
                throw new InvalidDataException("BLF 檔案結尾包含未完成的物件。 ");

            output.Flush();
            output.Dispose();
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

    private static void ValidateFileHeader(Stream input)
    {
        Span<byte> header = stackalloc byte[8];
        input.ReadExactly(header);
        if (!header[..4].SequenceEqual("LOGG"u8))
            throw new InvalidDataException("不是有效的 Vector BLF 檔案。 ");

        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        if (headerSize < 8 || headerSize > input.Length)
            throw new InvalidDataException("BLF 檔頭長度不正確。 ");
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
                throw new EndOfStreamException("BLF 容器檔頭不完整。 ");
            if (!baseHeader.AsSpan(0, 4).SequenceEqual("LOBJ"u8))
                throw new InvalidDataException("BLF 容器簽章錯誤。 ");

            uint objectSizeValue = ReadUInt32(baseHeader, 8);
            uint objectType = ReadUInt32(baseHeader, 12);
            if (objectType != LogContainer || objectSizeValue < 32 || objectSizeValue > int.MaxValue)
                throw new InvalidDataException("BLF 外層物件不是有效的記錄容器。 ");

            int objectSize = (int)objectSizeValue;
            byte[] body = new byte[objectSize - 16];
            input.ReadExactly(body);
            int padding = objectSize % 4;
            if (padding > 0)
                input.Position += padding;

            ushort compressionMethod = ReadUInt16(body, 0);
            uint uncompressedSizeValue = ReadUInt32(body, 8);
            if (uncompressedSizeValue > int.MaxValue)
                throw new InvalidDataException("BLF 解壓縮資料過大。 ");

            byte[] compressed = body[16..];
            if (compressionMethod == 0)
            {
                yield return compressed;
            }
            else if (compressionMethod == 2)
            {
                using var source = new MemoryStream(compressed, false);
                using var zlib = new ZLibStream(source, CompressionMode.Decompress);
                byte[] result = new byte[(int)uncompressedSizeValue];
                zlib.ReadExactly(result);
                yield return result;
            }
            else
            {
                throw new InvalidDataException($"不支援的 BLF 壓縮方法：{compressionMethod}。 ");
            }
        }
    }

    private static LinFrame ParseLinMessage2(ReadOnlySpan<byte> obj, int headerSize)
    {
        const int minimumBodySize = 124;
        if (headerSize < 32 || obj.Length < headerSize + minimumBodySize)
            throw new InvalidDataException("LIN_MESSAGE2 物件長度不足。 ");

        ReadOnlySpan<byte> body = obj[headerSize..];
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(obj[16..]);
        ulong timestamp = BinaryPrimitives.ReadUInt64LittleEndian(obj[24..]);
        double seconds = ToSeconds(timestamp, flags);
        ushort channel = BinaryPrimitives.ReadUInt16LittleEndian(body[12..]);
        byte protectedId = body[37];
        byte id = (byte)(protectedId & 0x3F);
        byte dlc = (byte)Math.Min((int)body[38], 8);
        byte checksumModel = body[39];
        byte[] payload = body.Slice(112, dlc).ToArray();
        byte checksum = CalculateChecksum(protectedId, payload, checksumModel != 0);
        byte direction = body[122];
        return new LinFrame(seconds, channel, id, payload, checksum, checksumModel, direction);
    }

    private static LinFrame ParseLegacyLinMessage(ReadOnlySpan<byte> obj, int headerSize)
    {
        const int minimumBodySize = 20;
        if (headerSize < 32 || obj.Length < headerSize + minimumBodySize)
            throw new InvalidDataException("LIN_MESSAGE 物件長度不足。 ");

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

    private static void WriteCsvRow(TextWriter writer, int sequence, LinFrame frame)
    {
        byte pid = CalculateProtectedId(frame.Id);
        string data = string.Join(' ', frame.Data.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
        string checksumMode = frame.ChecksumModel == 0 ? "经典" : "增强";
        string dataType = frame.Direction == 0 ? "主机读" : "主机写";
        writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{sequence},{frame.Id:X2} [{pid:X2}],{data},{frame.Checksum:X2},{checksumMode},{dataType},{frame.Seconds:F3},LIN{frame.Channel},"));
    }

    private static double ToSeconds(ulong timestamp, uint flags) =>
        (flags & 1) != 0 ? timestamp * 0.00001 : timestamp * 0.000000001;

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
        int sum = CalculateProtectedId(id);
        foreach (byte value in data)
        {
            sum += value;
            if (sum > 255)
                sum -= 255;
        }
        return (byte)(~sum) == expected;
    }

    private static byte CalculateChecksum(byte protectedId, byte[] data, bool enhanced)
    {
        int sum = enhanced ? protectedId : 0;
        foreach (byte value in data)
        {
            sum += value;
            if (sum > 255)
                sum -= 255;
        }
        return (byte)~sum;
    }

    private static ushort ReadUInt16(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));

    private static uint ReadUInt32(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));

    private sealed record LinFrame(
        double Seconds, ushort Channel, byte Id, byte[] Data, byte Checksum, byte ChecksumModel, byte Direction);
}
