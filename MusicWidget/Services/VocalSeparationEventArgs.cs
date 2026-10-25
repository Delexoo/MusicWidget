using System;
using MusicWidget.Models;

namespace MusicWidget.Services;

public sealed class VocalSeparationEventArgs : EventArgs
{
    public VocalSeparationEventArgs(bool isBusy, string? status, Track? track, bool failed = false)
    {
        IsBusy = isBusy;
        Status = status;
        Track = track;
        Failed = failed;
    }

    public bool IsBusy { get; }
    public string? Status { get; }
    public Track? Track { get; }
    public bool Failed { get; }
}
