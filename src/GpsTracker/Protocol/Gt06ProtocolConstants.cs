namespace GpsTracker.Protocol;

public static class Gt06ProtocolConstants
{
    public const byte HeaderHigh = 0x78;
    public const byte HeaderLow = 0x78;
    public const byte ShortHeaderHigh = 0x79;
    public const byte ShortHeaderLow = 0x79;

    // Protocol numbers
    public const byte Login = 0x01;
    public const byte Location = 0x12;
    public const byte Status = 0x13;
    public const byte String = 0x15;
    public const byte Alarm = 0x16;
    public const byte GpsQuery = 0x1A;
    public const byte Command = 0x80;
    public const byte Heartbeat = 0x23;
    public const byte OnlineCommand = 0x15;

    // Server response protocol numbers
    public const byte LoginResponse = 0x01;
    public const byte LocationResponse = 0x12;
    public const byte HeartbeatResponse = 0x23;
}
