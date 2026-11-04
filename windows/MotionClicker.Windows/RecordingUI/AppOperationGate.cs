namespace MotionClicker.Windows.RecordingUI;
public enum AppOperation { Execution, Recording, Opening, ChoosingDestination }
/// <summary>One app-wide ownership gate. Paused execution retains its lease until explicit Stop.</summary>
public sealed class AppOperationGate {
    public bool Busy=>false;
    public IDisposable? TryEnter(AppOperation operation)=>null;
}
