namespace MediaBrowser.Controller.MediaEncoding;

/// <summary>
/// A span of the source, in seconds, whose audio is muted (EDL action 1).
/// </summary>
/// <param name="Start">Start of the window in seconds.</param>
/// <param name="End">End of the window in seconds.</param>
public readonly record struct EdlMuteWindow(double Start, double End);
