using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MoonlightHub.Core;

namespace MoonlightHub.UI;

/// <summary>Topmost PIN prompt shown when a Moonlight client starts pairing with one of the Sunshine instances.</summary>
public partial class PairingWindow : Window
{
    private readonly Hub _hub;
    private readonly InstanceSpec _spec;
    private PairingRequest _request;
    private bool _busy;
    private bool _closing;
    private readonly DispatcherTimer _resultTimeout = new() { Interval = TimeSpan.FromSeconds(20) };

    public int InstanceId => _spec.Id;

    public PairingWindow(Hub hub, InstanceSpec spec, PairingRequest request)
    {
        _hub = hub;
        _spec = spec;
        _request = request;
        InitializeComponent();
        _hub.Pairing.PairingFinished += OnPairingFinished;
        _hub.Pairing.PairingCancelled += OnPairingCancelled;
        _resultTimeout.Tick += (_, _) =>
        {
            _resultTimeout.Stop();
            if (_busy)
            {
                _busy = false;
                PairButton.IsEnabled = true;
                SetStatus("Sunshine 已收到 PIN，但客户端没有完成后续握手。若平板提示失败，请重新发起配对并再次输入。", false);
            }
        };
        UpdateRequest(request);
        Loaded += (_, _) => { PinBox.Focus(); Keyboard.Focus(PinBox); };
        Closed += (_, _) =>
        {
            _hub.Pairing.PairingFinished -= OnPairingFinished;
            _hub.Pairing.PairingCancelled -= OnPairingCancelled;
            _resultTimeout.Stop();
        };
    }

    private void OnPairingCancelled(int instanceId)
    {
        if (instanceId != _spec.Id || _request.RemoteAddress == "手动输入") return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_closing) return;
            _closing = true;
            SetStatus("配对会话已结束（客户端超时或实例重启），窗口即将关闭。", false);
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            t.Tick += (_, _) => { t.Stop(); Close(); };
            t.Start();
        });
    }

    public void UpdateRequest(PairingRequest request)
    {
        _request = request;
        DetailText.Text = $"实例 {_spec.Id} · {_spec.Name}（端口 {_spec.Port}） · 来自 {request.RemoteAddress}";
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            var last = request.RemoteAddress.Split('.').LastOrDefault() ?? request.RemoteAddress;
            NameBox.Text = $"Moonlight-{last}";
        }
        SetStatus(_hub.Settings.GetUser(_spec.Id) == null
            ? "该实例还没有 Web 凭据，Hub 会先自动生成（可能需要几秒）。"
            : "输入 PIN 后按回车即可。", false);
        PairButton.IsEnabled = true;
        _busy = false;
    }

    private void SetStatus(string text, bool ok, bool error = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = (Brush)FindResource(error ? "DangerBrush" : ok ? "SuccessBrush" : "MutedBrush");
    }

    private void PinBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var digits = new string(PinBox.Text.Where(char.IsDigit).ToArray());
        if (digits != PinBox.Text)
        {
            PinBox.Text = digits;
            PinBox.CaretIndex = digits.Length;
        }
    }

    private void PinBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Pair_Click(sender, e);
        }
        else if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    private async void Pair_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var pin = PinBox.Text.Trim();
        if (pin.Length != 4)
        {
            SetStatus("PIN 必须是 4 位数字。", false, true);
            return;
        }
        _busy = true;
        PairButton.IsEnabled = false;
        SetStatus("正在把 PIN 发送给 Sunshine…", false);
        var name = NameBox.Text.Trim();
        try
        {
            if (_hub.Settings.GetPassword(_spec.Id) == null)
            {
                SetStatus("正在为该实例生成 Web 凭据…", false);
                var got = await _hub.Instances.EnsureCredentialsAsync(_spec, allowRestart: true);
                if (!got)
                {
                    SetStatus("无法自动生成凭据（实例正在串流）。请先在“客户端”页设置凭据。", false, true);
                    _busy = false;
                    PairButton.IsEnabled = true;
                    return;
                }
                // Restarting Sunshine drops the pending pairing session; the client must start pairing again.
                SetStatus("凭据已生成，Sunshine 已重启。请在平板上重新发起配对，再输入新的 PIN。", false, true);
                _busy = false;
                PairButton.IsEnabled = true;
                _hub.Pairing.ClearPending(_spec.Id);
                return;
            }

            var (ok, msg) = await _hub.Instances.Api(_spec).PairAsync(pin, name);
            if (!ok)
            {
                SetStatus(msg, false, true);
                _busy = false;
                PairButton.IsEnabled = true;
                return;
            }
            SetStatus("PIN 已提交，等待客户端完成握手…", false);
            _resultTimeout.Start();
        }
        catch (Exception ex)
        {
            SetStatus("配对出错: " + ex.Message, false, true);
            _busy = false;
            PairButton.IsEnabled = true;
        }
    }

    private void OnPairingFinished(int instanceId, string uniqueId, bool success, string clientName)
    {
        if (instanceId != _spec.Id) return;
        Dispatcher.BeginInvoke(() =>
        {
            _resultTimeout.Stop();
            _busy = false;
            if (success)
            {
                SetStatus($"配对成功：{(string.IsNullOrWhiteSpace(clientName) ? "客户端" : clientName)} 现在可以连接了。", true);
                PairButton.IsEnabled = false;
                if (!_closing)
                {
                    _closing = true;
                    var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
                    t.Tick += (_, _) => { t.Stop(); Close(); };
                    t.Start();
                }
            }
            else
            {
                SetStatus("PIN 不正确或握手失败。请在平板上重新发起配对，然后输入新的 PIN。", false, true);
                PinBox.Text = string.Empty;
                PinBox.Focus();
                PairButton.IsEnabled = true;
            }
        });
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
