using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using static MotionClicker.Core.Recording.RecordingCheck;
namespace MotionClicker.Core.Recording;

public enum PointerTimingBasis { NativeHostObservation }
public enum PointerHostClock { MachAbsolute, QueryPerformanceCounter }
public enum PointerPacketClock { CgEventTimestamp, WindowsLowLevelMouseTime }
public enum PointerPacketUnits { Nanoseconds, Milliseconds }

public sealed record PointerTiming(PointerTimingBasis Basis, PointerHostClock HostClock, PointerPacketClock PacketClock,
    PointerPacketUnits PacketUnits, string PacketTimestamp, long OwnerDelayUs)
{
    internal void Validate()
    {
        Require(Enum.IsDefined(Basis) && OwnerDelayUs>=0 && PacketTimestamp is { Length: >0 and <=20 } &&
            PacketTimestamp.All(c=>c is >= '0' and <= '9') && (PacketTimestamp=="0" || PacketTimestamp[0]!='0'),"Invalid pointer timing diagnostic");
        Require(ulong.TryParse(PacketTimestamp,NumberStyles.None,CultureInfo.InvariantCulture,out var stamp),"Pointer packet timestamp overflow");
        Require((HostClock,PacketClock,PacketUnits) switch {
            (PointerHostClock.MachAbsolute,PointerPacketClock.CgEventTimestamp,PointerPacketUnits.Nanoseconds)=>true,
            (PointerHostClock.QueryPerformanceCounter,PointerPacketClock.WindowsLowLevelMouseTime,PointerPacketUnits.Milliseconds)=>stamp<=uint.MaxValue,
            _=>false },"Incompatible pointer clocks/units");
    }
}
internal sealed class PointerTimingJsonConverter : JsonConverter<PointerTiming>
{
    private sealed record Wire(PointerTimingBasis Basis,PointerHostClock HostClock,PointerPacketClock PacketClock,
        PointerPacketUnits PacketUnits,string PacketTimestamp,long OwnerDelayUs);
    public override PointerTiming Read(ref Utf8JsonReader reader,Type type,JsonSerializerOptions options)
    {
        var w=JsonSerializer.Deserialize<Wire>(ref reader,options)??throw new JsonException("Null pointer timing");
        var result=new PointerTiming(w.Basis,w.HostClock,w.PacketClock,w.PacketUnits,w.PacketTimestamp,w.OwnerDelayUs);
        result.Validate();return result;
    }
    public override void Write(Utf8JsonWriter writer,PointerTiming value,JsonSerializerOptions options)
    {
        value.Validate();JsonSerializer.Serialize(writer,new Wire(value.Basis,value.HostClock,value.PacketClock,value.PacketUnits,value.PacketTimestamp,value.OwnerDelayUs),options);
    }
}
internal sealed class PointerTimingValidation
{
    private PointerHostClock? hostClock;
    public void Accept(CapturedPointerEvent e,PointerTimingBasis? basis)
    {
        Require(e.Timing?.Basis==basis,"Pointer/take timing basis mismatch");
        if(e.Timing is PointerTiming t)
        {
            t.Validate();Require(e.HostTimeUs>=0&&(hostClock is null||hostClock==t.HostClock),"Invalid or mixed observation host clocks");
            hostClock=t.HostClock;
        }
    }
}
