using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PairUp.App.Audio;
using QRCoder;

namespace PairUp.App.Services;

/// <summary>
/// Embeds a small local web server so a guest can control the volume of their own connected
/// device from their phone's browser — no app install, just the same Wi-Fi network. Scoped
/// deliberately narrow: a link only ever exposes volume/mute for the one device it was
/// generated for, and every request needs the session PIN shown in the app.
/// </summary>
public sealed class GuestServer : IDisposable
{
    public const int Port = 51888;

    private readonly Func<IEnumerable<AudioDeviceInfo>> _getDevices;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, double> _preMuteVolume = new();
    private WebApplication? _app;

    public string Pin { get; } = Random.Shared.Next(100000, 999999).ToString();
    public string? LocalIp { get; private set; }
    public bool IsRunning { get; private set; }

    public GuestServer(Func<IEnumerable<AudioDeviceInfo>> getDevices, Dispatcher dispatcher)
    {
        _getDevices = getDevices;
        _dispatcher = dispatcher;
    }

    public void Start()
    {
        if (IsRunning) return;

        LocalIp = GetLocalIPv4();
        if (LocalIp is null) return;

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://{LocalIp}:{Port}");
        var app = builder.Build();

        app.MapGet("/d/{deviceId}", (string deviceId, string? pin) => HandlePage(deviceId, pin));
        app.MapGet("/api/d/{deviceId}/state", (string deviceId, string? pin) => HandleState(deviceId, pin));
        app.MapPost("/api/d/{deviceId}/volume", (string deviceId, string? pin, VolumeBody body) => HandleSetVolume(deviceId, pin, body));
        app.MapPost("/api/d/{deviceId}/mute", (string deviceId, string? pin, MuteBody body) => HandleSetMute(deviceId, pin, body));
        app.MapPost("/api/d/{deviceId}/eq", (string deviceId, string? pin, EqBody body) => HandleSetEq(deviceId, pin, body));

        _app = app;
        _ = app.RunAsync();
        IsRunning = true;
    }

    public string GetLinkForDevice(string deviceId) =>
        $"http://{LocalIp}:{Port}/d/{Uri.EscapeDataString(deviceId)}?pin={Pin}";

    public byte[] GetQrPng(string url)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        var pngQr = new PngByteQRCode(data);
        return pngQr.GetGraphic(12);
    }

    private AudioDeviceInfo? FindDevice(string deviceId) =>
        _dispatcher.Invoke(() => _getDevices().FirstOrDefault(d => d.Id == deviceId));

    private IResult HandlePage(string deviceId, string? pin)
    {
        if (pin != Pin) return Results.Text(ErrorHtml("Wrong PIN."), "text/html", statusCode: 403);

        var device = FindDevice(deviceId);
        if (device is null) return Results.Text(ErrorHtml("Device not found."), "text/html", statusCode: 404);

        return Results.Text(PageHtml(deviceId, pin, device.Name), "text/html");
    }

    private IResult HandleState(string deviceId, string? pin)
    {
        if (pin != Pin) return Results.Json(new { error = "forbidden" }, statusCode: 403);

        var device = FindDevice(deviceId);
        if (device is null) return Results.Json(new { error = "not found" }, statusCode: 404);

        return Results.Json(new
        {
            name = device.Name,
            connected = device.IsConnected,
            volume = device.Volume,
            muted = device.Volume <= 0,
            bassBoost = device.BassBoost,
            treble = device.Treble,
            isMono = device.IsMono
        });
    }

    private IResult HandleSetVolume(string deviceId, string? pin, VolumeBody body)
    {
        if (pin != Pin) return Results.Json(new { error = "forbidden" }, statusCode: 403);

        var device = FindDevice(deviceId);
        if (device is null || !device.IsConnected) return Results.Json(new { error = "unavailable" }, statusCode: 404);

        var clamped = Math.Clamp(body.Volume, 0, 100);
        _dispatcher.Invoke(() => device.Volume = clamped);
        return Results.Ok();
    }

    private IResult HandleSetMute(string deviceId, string? pin, MuteBody body)
    {
        if (pin != Pin) return Results.Json(new { error = "forbidden" }, statusCode: 403);

        var device = FindDevice(deviceId);
        if (device is null || !device.IsConnected) return Results.Json(new { error = "unavailable" }, statusCode: 404);

        _dispatcher.Invoke(() =>
        {
            if (body.Muted)
            {
                _preMuteVolume[deviceId] = device.Volume > 0 ? device.Volume : 75;
                device.Volume = 0;
            }
            else
            {
                device.Volume = _preMuteVolume.GetValueOrDefault(deviceId, 75);
            }
        });

        return Results.Ok();
    }

    private IResult HandleSetEq(string deviceId, string? pin, EqBody body)
    {
        if (pin != Pin) return Results.Json(new { error = "forbidden" }, statusCode: 403);

        var device = FindDevice(deviceId);
        if (device is null || !device.IsConnected) return Results.Json(new { error = "unavailable" }, statusCode: 404);

        _dispatcher.Invoke(() =>
        {
            device.BassBoost = Math.Clamp(body.BassBoost, -12, 12);
            device.Treble = Math.Clamp(body.Treble, -12, 12);
            device.IsMono = body.IsMono;
        });

        return Results.Ok();
    }

    private static string? GetLocalIPv4()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

            foreach (var addr in ni.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(addr.Address))
                {
                    return addr.Address.ToString();
                }
            }
        }
        return null;
    }

    private static string ErrorHtml(string message) => $"""
        <!DOCTYPE html><html><body style="font-family:sans-serif;background:#14120F;color:#f3ede1;
        display:flex;align-items:center;justify-content:center;height:100vh;margin:0;">
        <p>{message}</p></body></html>
        """;

    private static string PageHtml(string deviceId, string pin, string deviceName) => $$"""
        <!DOCTYPE html>
        <html>
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1, maximum-scale=1">
        <title>{{deviceName}} — PairUp</title>
        <style>
          *{box-sizing:border-box;}
          body{
            margin:0; min-height:100vh; display:flex; align-items:center; justify-content:center;
            background:#14120F; color:#f3ede1; font-family:-apple-system,"Segoe UI",sans-serif;
            padding:24px;
          }
          .card{width:100%; max-width:360px; text-align:center;}
          .eyebrow{font-size:12px; letter-spacing:.1em; text-transform:uppercase; color:#6f6455; margin-bottom:8px;}
          .name{font-size:24px; font-weight:600; margin-bottom:40px;}
          .volume-value{font-size:56px; font-weight:700; color:#33e0c0; margin-bottom:24px; font-variant-numeric:tabular-nums;}
          input[type=range]{
            width:100%; height:8px; border-radius:4px; background:#2C261E;
            -webkit-appearance:none; appearance:none; margin-bottom:32px;
          }
          input[type=range]::-webkit-slider-thumb{
            -webkit-appearance:none; width:28px; height:28px; border-radius:50%;
            background:#33e0c0; cursor:pointer; border:4px solid #14120F;
          }
          button{
            width:100%; padding:16px; border-radius:12px; border:1px solid #4A4033;
            background:#242019; color:#f3ede1; font-size:15px; font-weight:600; cursor:pointer;
          }
          button.muted{background:#33e0c026; border-color:#33e0c0; color:#33e0c0;}
          .offline{color:#F0554C; font-size:14px; margin-top:20px;}

          .eq{margin-top:36px; padding-top:28px; border-top:1px solid #4A4033;}
          .eq-row{margin-bottom:22px;}
          .eq-label{display:flex; justify-content:space-between; margin-bottom:8px;}
          .eq-label .name{font-size:12px; letter-spacing:.1em; text-transform:uppercase; color:#6f6455; font-weight:600; margin:0;}
          .eq-label .val{font-size:13px; color:#a89c89; font-variant-numeric:tabular-nums;}
          input[type=range].eq-slider{margin-bottom:6px; background:#242019;}
          input[type=range].eq-slider::-webkit-slider-thumb{width:22px; height:22px; border-width:3px;}
          .eq-range{display:flex; justify-content:space-between; font-size:10px; color:#6f6455;}
          .mono-row{display:flex; align-items:center; justify-content:space-between;}
          .toggle{
            position:relative; width:46px; height:26px; border-radius:100px;
            background:#2C261E; border:1px solid #4A4033; cursor:pointer; flex:none;
          }
          .toggle.on{background:#33e0c026; border-color:#33e0c0;}
          .toggle i{
            position:absolute; top:2px; left:2px; width:20px; height:20px; border-radius:50%;
            background:#6f6455; transition:transform .15s ease, background .15s ease;
          }
          .toggle.on i{transform:translateX(20px); background:#33e0c0;}
        </style>
        </head>
        <body>
          <div class="card">
            <div class="eyebrow">Now controlling</div>
            <div class="name">{{deviceName}}</div>
            <div class="volume-value" id="vol">--</div>
            <input type="range" id="slider" min="0" max="100" value="0">
            <button id="muteBtn">Mute</button>
            <div class="offline" id="offline" style="display:none;">Device disconnected</div>

            <div class="eq">
              <div class="eq-row">
                <div class="eq-label"><p class="name">Bass</p><span class="val" id="bassVal">0dB</span></div>
                <input type="range" class="eq-slider" id="bassSlider" min="-12" max="12" value="0">
                <div class="eq-range"><span>-12dB</span><span>+12dB</span></div>
              </div>
              <div class="eq-row">
                <div class="eq-label"><p class="name">Treble</p><span class="val" id="trebleVal">0dB</span></div>
                <input type="range" class="eq-slider" id="trebleSlider" min="-12" max="12" value="0">
                <div class="eq-range"><span>-12dB</span><span>+12dB</span></div>
              </div>
              <div class="mono-row">
                <p class="name">Mono</p>
                <div class="toggle" id="monoToggle"><i></i></div>
              </div>
            </div>
          </div>
          <script>
            const deviceId = {{JsonSerializer.Serialize(deviceId)}};
            const pin = {{JsonSerializer.Serialize(pin)}};
            const base = `/api/d/${encodeURIComponent(deviceId)}`;
            const slider = document.getElementById('slider');
            const vol = document.getElementById('vol');
            const muteBtn = document.getElementById('muteBtn');
            const offline = document.getElementById('offline');
            const bassSlider = document.getElementById('bassSlider');
            const trebleSlider = document.getElementById('trebleSlider');
            const bassVal = document.getElementById('bassVal');
            const trebleVal = document.getElementById('trebleVal');
            const monoToggle = document.getElementById('monoToggle');
            let dragging = false;
            let eqDragging = false;
            let lastMuted = false;
            let lastMono = false;

            function fmtDb(v) {
              const n = Math.round(v);
              return (n > 0 ? '+' : '') + n + 'dB';
            }

            async function refresh() {
              try {
                const r = await fetch(`${base}/state?pin=${pin}`);
                if (!r.ok) return;
                const s = await r.json();
                offline.style.display = s.connected ? 'none' : 'block';
                if (!dragging) {
                  slider.value = s.volume;
                  vol.textContent = Math.round(s.volume) + '%';
                }
                lastMuted = s.muted;
                muteBtn.textContent = s.muted ? 'Unmute' : 'Mute';
                muteBtn.className = s.muted ? 'muted' : '';

                if (!eqDragging) {
                  bassSlider.value = s.bassBoost;
                  trebleSlider.value = s.treble;
                  bassVal.textContent = fmtDb(s.bassBoost);
                  trebleVal.textContent = fmtDb(s.treble);
                  lastMono = s.isMono;
                  monoToggle.className = s.isMono ? 'toggle on' : 'toggle';
                }
              } catch {}
            }

            async function pushEq() {
              await fetch(`${base}/eq?pin=${pin}`, {
                method: 'POST', headers: {'Content-Type':'application/json'},
                body: JSON.stringify({
                  bassBoost: Number(bassSlider.value),
                  treble: Number(trebleSlider.value),
                  isMono: lastMono
                })
              });
            }

            slider.addEventListener('input', () => { dragging = true; vol.textContent = slider.value + '%'; });
            slider.addEventListener('change', async () => {
              await fetch(`${base}/volume?pin=${pin}`, {
                method: 'POST', headers: {'Content-Type':'application/json'},
                body: JSON.stringify({ volume: Number(slider.value) })
              });
              dragging = false;
            });

            muteBtn.addEventListener('click', async () => {
              await fetch(`${base}/mute?pin=${pin}`, {
                method: 'POST', headers: {'Content-Type':'application/json'},
                body: JSON.stringify({ muted: !lastMuted })
              });
              refresh();
            });

            bassSlider.addEventListener('input', () => { eqDragging = true; bassVal.textContent = fmtDb(bassSlider.value); });
            trebleSlider.addEventListener('input', () => { eqDragging = true; trebleVal.textContent = fmtDb(trebleSlider.value); });
            bassSlider.addEventListener('change', async () => { await pushEq(); eqDragging = false; });
            trebleSlider.addEventListener('change', async () => { await pushEq(); eqDragging = false; });

            monoToggle.addEventListener('click', async () => {
              lastMono = !lastMono;
              monoToggle.className = lastMono ? 'toggle on' : 'toggle';
              await pushEq();
            });

            refresh();
            setInterval(refresh, 1000);
          </script>
        </body>
        </html>
        """;

    public void Dispose()
    {
        _app?.StopAsync().GetAwaiter().GetResult();
        IsRunning = false;
    }
}

public sealed record VolumeBody(double Volume);
public sealed record MuteBody(bool Muted);
public sealed record EqBody(double BassBoost, double Treble, bool IsMono);
