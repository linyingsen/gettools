using System.Text;

namespace PromptCrawlerApp;

public sealed class MainForm : Form
{
    private readonly Button _btnStart;
    private readonly TextBox _txtLog;

    public MainForm()
    {
        Text = "Prompt 采集器";
        Width = 900;
        Height = 650;
        StartPosition = FormStartPosition.CenterScreen;

        _btnStart = new Button
        {
            Text = "开始",
            Width = 120,
            Height = 40,
            Left = 20,
            Top = 20
        };
        _btnStart.Click += BtnStart_Click;

        _txtLog = new TextBox
        {
            Left = 20,
            Top = 80,
            Width = 840,
            Height = 510,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            ReadOnly = true,
            Font = new Font("Consolas", 10)
        };

        Controls.Add(_btnStart);
        Controls.Add(_txtLog);
    }

    private async void BtnStart_Click(object? sender, EventArgs e)
    {
        _btnStart.Enabled = false;
        _txtLog.Clear();
        Log("开始执行采集...");

        try
        {
            using var httpClient = CrawlerBootstrap.BuildHttpClient();
            var crawler = new PromptCrawler(httpClient, new Uri("https://ai.codefather.cn"), Log);

            var prompts = await crawler.CrawlAllAsync();
            Log($"采集完成，共 {prompts.Count} 条。正在写入数据库...");

            await using var connection = new Microsoft.Data.SqlClient.SqlConnection(CrawlerBootstrap.ConnectionString);
            await connection.OpenAsync();

            await PromptRepository.EnsureTableAsync(connection);
            var affected = await PromptRepository.UpsertAsync(connection, prompts);

            Log($"数据库写入完成，影响行数：{affected}");
            Log("全部执行结束。") ;
        }
        catch (Exception ex)
        {
            Log("执行失败：" + ex.Message);
        }
        finally
        {
            _btnStart.Enabled = true;
        }
    }

    private void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";

        if (_txtLog.InvokeRequired)
        {
            _txtLog.Invoke(() => _txtLog.AppendText(line));
            return;
        }

        _txtLog.AppendText(line);
    }
}
