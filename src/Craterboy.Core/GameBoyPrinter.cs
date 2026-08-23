namespace Craterboy;

public readonly record struct PrinterImage(
    ReadOnlyMemory<byte> GrayscalePixels, int Width, int Height,
    byte TopMargin, byte BottomMargin, byte Exposure);

public interface IPrinterSink
{
    void Print(PrinterImage image);
}

public sealed class GameBoyPrinter(IPrinterSink? sink = null) : ISerialEndpoint
{
    private enum PacketState { Magic1, Magic2, Command, Compression, LengthLow, LengthHigh, Data, ChecksumLow, ChecksumHigh, Active, Status }

    private readonly byte[] _commandData = new byte[0x280];
    private readonly byte[] _image = new byte[160 * 200];
    private PacketState _state;
    private byte _command;
    private byte _response;
    private byte _status;
    private bool _compressed;
    private int _encodedBytesLeft;
    private int _commandLength;
    private int _imageLength;
    private ushort _checksum;
    private ushort _receivedChecksum;
    private int _runLength;
    private bool _repeatRun;

    public byte Status => _status;

    public byte Exchange(byte outgoing)
    {
        var response = _response;
        _response = 0;
        Receive(outgoing);
        return response;
    }

    private void Receive(byte value)
    {
        switch (_state)
        {
            case PacketState.Magic1:
                if (value != 0x88) return;
                _status &= 0xFE;
                _commandLength = 0;
                _checksum = 0;
                _runLength = 0;
                _state = PacketState.Magic2;
                return;
            case PacketState.Magic2:
                if (value == 0x88) return;
                if (value != 0x33) { _state = PacketState.Magic1; return; }
                _state = PacketState.Command;
                return;
            case PacketState.Command:
                _command = (byte)(value & 0x0F);
                AddChecksum(value);
                _state = PacketState.Compression;
                return;
            case PacketState.Compression:
                _compressed = (value & 1) != 0;
                AddChecksum(value);
                _state = PacketState.LengthLow;
                return;
            case PacketState.LengthLow:
                _encodedBytesLeft = value;
                AddChecksum(value);
                _state = PacketState.LengthHigh;
                return;
            case PacketState.LengthHigh:
                _encodedBytesLeft |= (value & 3) << 8;
                AddChecksum(value);
                _state = _encodedBytesLeft == 0 ? PacketState.ChecksumLow : PacketState.Data;
                return;
            case PacketState.Data:
                AddChecksum(value);
                Decode(value);
                if (--_encodedBytesLeft == 0) _state = PacketState.ChecksumLow;
                return;
            case PacketState.ChecksumLow:
                _receivedChecksum = value;
                _state = PacketState.ChecksumHigh;
                return;
            case PacketState.ChecksumHigh:
                _receivedChecksum |= (ushort)(value << 8);
                if (_receivedChecksum != _checksum)
                {
                    _status |= 1;
                    _state = PacketState.Magic1;
                    return;
                }
                _response = 0x81;
                _state = PacketState.Active;
                return;
            case PacketState.Active:
                _response = _command == 1 ? (byte)0 : _status;
                _state = PacketState.Status;
                return;
            case PacketState.Status:
                HandleCommand();
                _state = PacketState.Magic1;
                return;
        }
    }

    private void Decode(byte value)
    {
        if (!_compressed)
        {
            Append(value);
            return;
        }
        if (_runLength == 0)
        {
            _repeatRun = (value & 0x80) != 0;
            _runLength = (value & 0x7F) + (_repeatRun ? 2 : 1);
        }
        else if (_repeatRun)
        {
            while (_runLength > 0)
            {
                Append(value);
                _runLength--;
            }
        }
        else
        {
            Append(value);
            _runLength--;
        }
    }

    private void HandleCommand()
    {
        switch (_command)
        {
            case 1:
                _status = 0;
                _imageLength = 0;
                break;
            case 2 when _commandLength == 4:
                _status = 6;
                var palette = _commandData[2];
                var pixels = new byte[_imageLength];
                byte[] colors = [255, 170, 85, 0];
                for (var index = 0; index < pixels.Length; index++)
                    pixels[index] = colors[(palette >> (_image[index] * 2)) & 3];
                sink?.Print(new(pixels, 160, pixels.Length / 160,
                    (byte)(_commandData[1] >> 4), (byte)(_commandData[1] & 7), (byte)(_commandData[3] & 0x7F)));
                _imageLength = 0;
                break;
            case 4 when _commandLength == 0x280:
                DecodeTiles();
                _status = 8;
                break;
        }
    }

    private void DecodeTiles()
    {
        if (_imageLength + 16 * 160 > _image.Length) _imageLength = 0;
        var source = 0;
        for (var tileRow = 0; tileRow < 2; tileRow++)
        {
            for (var tileX = 0; tileX < 20; tileX++)
                for (var y = 0; y < 8; y++)
                {
                    var low = _commandData[source++];
                    var high = _commandData[source++];
                    for (var x = 0; x < 8; x++)
                        _image[_imageLength + y * 160 + tileX * 8 + x] =
                            (byte)(((low >> (7 - x)) & 1) | (((high >> (7 - x)) & 1) << 1));
                }
            _imageLength += 8 * 160;
        }
    }

    private void Append(byte value)
    {
        if (_commandLength < _commandData.Length) _commandData[_commandLength++] = value;
    }

    private void AddChecksum(byte value) => _checksum = unchecked((ushort)(_checksum + value));
}
