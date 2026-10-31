using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using NetControl.Models;

namespace NetControl.Services;

public class BandwidthSample
{
    public DateTime Time { get; set; }
    public double DownloadBps { get; set; }   // bytes per second
    public double UploadBps { get; set; }
}

/// <summary>
/// Measures real interface byte counters once per second and derives live
/// up/down speeds plus a rolling history. No simulated data is produced:
/// if no adapter is selected, speeds read 0 and the UI says why.
/// </summary>
public class TrafficMonitor : IDisposable
{
    private System.Timers.Timer _timer;
    private readonly object _gate = new();
    private long _lastSent = -1;
    private long _lastRecv = -1;
    private DateTime _lastSample = DateTime.MinValue;

    public AdapterInfo Adapter { get; private set; }
    public double CurrentUploadBps { get; private set; }
    public double CurrentDownloadBps { get; private set; }
    public double SessionUploadBytes { get; private set; }
    public double SessionDownloadBytes { get; private set; }

    private readonly List<BandwidthSample> _history = new();
    public IReadOnlyList<BandwidthSample> History
    {
        get { lock (_gate) return _history.ToList().AsReadOnly(); }
    }

    public event Action SampleTaken;

    public void Start(AdapterInfo adapter)
    {
        Adapter = adapter;
        lock (_gate) { _lastSent = -1; _lastRecv = -1; _lastSample = DateTime.MinValue; }
        if (_timer == null)
        {
            _timer = new System.Timers.Timer(1000) { AutoReset = true };
            _timer.Elapsed += (_, _) => Sample();
        }
        _timer.Start();
    }

    public void SetAdapter(AdapterInfo adapter)
    {
        Start(adapter);
    }

    private void Sample()
    {
        try
        {
            if (Adapter == null || string.IsNullOrEmpty(Adapter.Id)) return;
            var ni = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.Id == Adapter.Id);
            if (ni == null) return;

            var stats = ni.GetIPStatistics();
            long sent = stats.BytesSent;
            long recv = stats.BytesReceived;
            var now = DateTime.Now;

            lock (_gate)
            {
                if (_lastSent >= 0 && now > _lastSample)
                {
                    double sec = (now - _lastSample).TotalSeconds;
                    if (sec > 0.2 && sec < 10)
                    {
                        CurrentUploadBps = Math.Max(0, sent - _lastSent) / sec;
                        CurrentDownloadBps = Math.Max(0, recv - _lastRecv) / sec;
                        SessionUploadBytes += CurrentUploadBps * sec;
                        SessionDownloadBytes += CurrentDownloadBps * sec;

                        _history.Add(new BandwidthSample
                        {
                            Time = now,
                            UploadBps = CurrentUploadBps,
                            DownloadBps = CurrentDownloadBps
                        });
                        if (_history.Count > 600) _history.RemoveAt(0);
                    }
                }
                _lastSent = sent;
                _lastRecv = recv;
                _lastSample = now;
            }
            SampleTaken?.Invoke();
        }
        catch { }
    }

    public (double up, double down) CurrentSpeeds()
    {
        lock (_gate) return (CurrentUploadBps, CurrentDownloadBps);
    }

    public void Dispose() => _timer?.Dispose();
}
