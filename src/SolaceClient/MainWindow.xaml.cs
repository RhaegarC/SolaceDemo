using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using SolaceClient.Services;

namespace SolaceClient;

public partial class MainWindow : Window
{
    /// <summary>
    /// What the JSON tab starts with: a small event envelope, which is the shape the test
    /// topic is for.
    ///
    /// Set from code rather than from the XAML Text attribute, which cannot carry it: XML
    /// normalises the newlines in an attribute value to spaces, and a value beginning with
    /// "{" is read as a markup extension rather than as text.
    /// </summary>
    private const string SampleJson = """
        {
          "system": "sysname",
          "event": "case.created",
          "caseId": "00000000-0000-0000-0000-000000000000",
          "sentAt": "2026-01-01T00:00:00Z"
        }
        """;

    private readonly DemoOptions _options = DemoOptions.Load();
    private readonly SolacePublisher _publisher;
    private string _caseId = NewCaseId();

    public MainWindow()
    {
        InitializeComponent();

        _publisher = new SolacePublisher(_options);
        _publisher.StatusChanged += OnPublisherStatusChanged;

        DataContext = this;
        CaseIdBox.Text = _caseId;

        // Both come from configuration, so they are filled in here rather than on connect:
        // they describe where the tab sends and what it sends, which do not depend on the
        // broker being reachable.
        JsonTopicBox.Text = _options.TestTopic;
        JsonBodyBox.Text = SampleJson;

        UpdateStatus($"Connecting to {_options.Host} …");

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    public ObservableCollection<AttachmentItem> Attachments { get; } = [];

    private static string NewCaseId() => Guid.NewGuid().ToString("D");

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await Task.Run(_publisher.Connect);

            var identityNote = _publisher.ClientCertificateSubject is { } subject
                ? $", client certificate {subject}"
                : string.Empty;
            var validationNote = _options.ValidateServerCertificate ? string.Empty : ", broker NOT authenticated";

            UpdateStatus($"Connected to {_options.Host}{identityNote}{validationNote}.");

            // The attachment limits belong to the tab that stages files, not to the
            // connection, so they are stated there rather than on the shared status line.
            var brokerLimit = _publisher.BrokerMaxGuaranteedMessageBytes;
            var brokerNote = brokerLimit > 0 ? $" The broker guarantees {brokerLimit:N0} bytes per message." : string.Empty;

            LimitsHint.Text =
                $"A file above {_options.ChunkSizeBytes:N0} bytes is split and reassembled by the consumer; "
                + $"one at or over {_options.MaxFileBytes:N0} bytes is refused.{brokerNote}";

            if (_publisher.ClientCertificateSubject is { } identity)
            {
                Log($"client certificate {identity}");
            }

            if (!_options.ValidateServerCertificate)
            {
                // Loud, because this is the check that the process is talking to the
                // broker it thinks it is. With it off, anything holding a valid
                // certificate for the host name is accepted.
                Log("WARNING: broker certificate validation is disabled — the broker is not authenticated");
            }
        }
        catch (Exception ex)
        {
            UpdateStatus($"Not connected: {ex.Message}");
            Log($"connect failed: {ex.Message}");
        }
    }

    private void OnClosed(object? sender, EventArgs e) => _publisher.Dispose();

    private void NewCaseButton_Click(object sender, RoutedEventArgs e)
    {
        _caseId = NewCaseId();
        CaseIdBox.Text = _caseId;
        Attachments.Clear();
        Log($"new case {_caseId}");
    }

    private void AddFilesButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Title = "Select attachments for this case",
        };

        if (dialog.ShowDialog(this) != true) return;

        foreach (var path in dialog.FileNames)
        {
            var item = new AttachmentItem(path, _options.MaxFileBytes, _options.ChunkSizeBytes);
            Attachments.Add(item);

            Log(item.IsRefused
                ? $"refused  {item.FileName} — {item.RefusalReason}"
                : item.IsChunked
                    ? $"staged   {item.FileName} ({item.SizeText}) — {item.ChunkCount:N0} chunks"
                    : $"staged   {item.FileName} ({item.SizeText})");
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        Attachments.Clear();
        LogBox.Clear();
    }

    private async void PublishButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_publisher.IsConnected)
        {
            Log("not connected — nothing published");
            return;
        }

        var staged = Attachments.Where(item => !item.IsRefused).ToList();
        if (staged.Count == 0)
        {
            Log("nothing staged to publish");
            return;
        }

        SetBusy(true);
        Log($"publishing {staged.Count} attachment(s) for case {_caseId}");

        var published = 0;
        var messages = 0;
        foreach (var item in staged)
        {
            // Re-measure here as well: the file may have grown since it was staged, and
            // the ceiling has to hold at the moment of sending, not only at selection.
            var currentSize = new FileInfo(item.FullPath).Length;
            if (currentSize >= _options.MaxFileBytes)
            {
                item.Status = "REFUSED at publish";
                Log($"refused  {item.FileName} — grew to {currentSize:N0} bytes");
                continue;
            }

            item.Status = "publishing…";
            try
            {
                var topic = AttachmentTopics.Build(_options.TopicPrefix, _caseId, item.FileName);
                var payload = await File.ReadAllBytesAsync(item.FullPath);
                var contentType = ContentTypes.For(item.FileName);

                var sent = await Task.Run(() => _publisher.PublishAttachment(
                    topic, _caseId, item.FileName, contentType, payload, _options.ChunkSizeBytes));

                item.Status = sent > 1 ? $"published ({sent:N0} chunks)" : "published";
                published++;
                messages += sent;

                Log(sent > 1
                    ? $"published {item.FileName} ({item.SizeText}) as {sent:N0} chunks"
                    : $"published {topic}");
            }
            catch (Exception ex)
            {
                // A file part-way through its chunks leaves the consumer holding an
                // incomplete transfer; it is abandoned there rather than written.
                item.Status = "failed";
                Log($"failed    {item.FileName} — {ex.Message}");
            }
        }

        Log($"done: {published}/{staged.Count} published, {messages} message(s)");
        SetBusy(false);
    }

    private async void SendJsonButton_Click(object sender, RoutedEventArgs e)
    {
        // Checked here as well as inside the publisher: without it the failure is an
        // InvalidOperationException surfacing through the generic catch below, which reads
        // as a fault rather than as the tab simply not being ready.
        if (!_publisher.IsConnected)
        {
            Log("not connected — nothing published");
            return;
        }

        var topic = _options.TestTopic;

        // Read once, on the UI thread. The delegate below runs on a worker, and touching a
        // TextBox's Text from there is not allowed.
        var body = JsonBodyBox.Text;

        SetBusy(true);
        try
        {
            // PublishJson parses and measures inside this delegate, so the JsonDocument is
            // created and disposed on one thread and never crosses back.
            var bytes = await Task.Run(() => _publisher.PublishJson(topic, body));
            Log($"published {bytes:N0} bytes of JSON to {topic}");
        }
        catch (JsonException ex)
        {
            Log($"refused   not valid JSON — {ex.Message}");
        }
        catch (ArgumentException ex)
        {
            // The payload's own problems: too large, or valid JSON that is not an object.
            Log($"refused   {ex.Message}");
        }
        catch (Exception ex)
        {
            Log($"failed    {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        PublishButton.IsEnabled = !busy;
        AddFilesButton.IsEnabled = !busy;
        NewCaseButton.IsEnabled = !busy;
        SendJsonButton.IsEnabled = !busy;
    }

    private void OnPublisherStatusChanged(string status) =>
        Dispatcher.Invoke(() => UpdateStatus(status));

    private void UpdateStatus(string text) => StatusText.Text = text;

    private void Log(string message)
    {
        LogBox.AppendText($"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }
}
