namespace GhostCode.Core.Monitoring;

public sealed record ActiveIdeStatus(
    bool DevCpp,
    bool VsCode,
    bool Python,
    bool Edge,
    bool Thorium)
{
    public string ActiveIdeSummary
    {
        get
        {
            var list = new List<string>();
            if (DevCpp) list.Add("Dev-C++");
            if (VsCode) list.Add("VS Code");
            if (Python) list.Add("Python");
            return list.Count == 0 ? "None" : string.Join(", ", list);
        }
    }
}

public static class IdeProcessDetector
{
    public static ActiveIdeStatus Detect()
    {
        var names = WindowsProcessSnapshot.GetRunningProcessNames();
        return new ActiveIdeStatus(
            names.Contains("devcpp"),
            names.Contains("code"),
            names.Contains("python") || names.Contains("pythonw"),
            names.Contains("msedge"),
            names.Contains("thorium"));
    }
}
