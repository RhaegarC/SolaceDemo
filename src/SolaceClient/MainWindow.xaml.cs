using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using SolaceClient.Services;

namespace SolaceClient;

public partial class MainWindow : Window
{
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

            var brokerLimit = _publisher.BrokerMaxGuaranteedMessageBytes;
            var brokerNote = brokerLimit > 0 ? $", broker guarantees up to {brokerLimit:N0} bytes" : string.Empty;
            UpdateStatus($"Connected. Cap {_options.MaxAttachmentBytes:N0} bytes{brokerNote}.");
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
            var item = new AttachmentItem(path, _options.MaxAttachmentBytes);
            Attachments.Add(item);

            Log(item.IsRefused
                ? $"refused  {item.FileName} — {item.RefusalReason}"
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
        foreach (var item in staged)
        {
            // Re-measure here as well: the file may have grown since it was staged, and
            // the cap has to hold at the moment of sending, not only at selection.
            var currentSize = new FileInfo(item.FullPath).Length;
            if (currentSize >= _options.MaxAttachmentBytes)
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

                await Task.Run(() => _publisher.Publish(topic, _caseId, item.FileName, contentType, payload));

                item.Status = "published";
                published++;
                Log($"published {topic}");
            }
            catch (Exception ex)
            {
                item.Status = "failed";
                Log($"failed    {item.FileName} — {ex.Message}");
            }
        }

        Log($"done: {published}/{staged.Count} published");
        SetBusy(false);
    }

    private void SetBusy(bool busy)
    {
        PublishButton.IsEnabled = !busy;
        AddFilesButton.IsEnabled = !busy;
        NewCaseButton.IsEnabled = !busy;
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
