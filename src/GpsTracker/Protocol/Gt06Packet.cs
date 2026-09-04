namespace GpsTracker.Protocol;

public enum Gt06PacketType
{
    Unknown,
    Login,
    Location,
    Status,
    Heartbeat,
    Alarm,
    Command
}

public class Gt06Packet
{
    public Gt06PacketType Type { get; set; }
    public string Imei { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double Speed { get; set; }
    public double Course { get; set; }
    public int Satellites { get; set; }
    public DateTime Timestamp { get; set; }
    public bool IsValidGps { get; set; }
    public byte ProtocolNumber { get; set; }
    public ushort SerialNumber { get; set; }
    public byte[] RawData { get; set; } = Array.Empty<byte>();
}
