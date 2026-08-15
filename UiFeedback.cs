using System;

// Operation-triggered feedback channel: failure sites call Fail(), the tray
// (the only balloon owner) subscribes and shows a non-intrusive balloon.
static class UiFeedback
{
    public static event Action<string> BalloonRequested;
    public static void Fail(string message)
    {
        Logging.Log("UiFeedback: " + message);
        var h = BalloonRequested;
        if (h != null) { try { h(message); } catch { } }
    }
}
