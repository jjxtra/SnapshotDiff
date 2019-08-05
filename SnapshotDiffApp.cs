using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using MailKit;
using MailKit.Net;
using MailKit.Net.Smtp;
using MimeKit;

using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace SnapshotDiff
{
    public class SnapshotDiffOptions
    {
        public SnapshotDiffOptions(string[] args)
        {
            if (args == null || args.Length == 0)
            {
                return;
            }

            foreach (string arg in args)
            {
                int pos = arg.IndexOf('=');
                if (pos < 0)
                {
                    throw new ArgumentException("Invalid argument " + arg);
                }
                string key = arg.Substring(0, pos).Trim('-', '/');
                string value = arg.Substring(++pos);
                bool found = false;
                foreach (PropertyInfo prop in GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (prop.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        prop.SetValue(this, Convert.ChangeType(value, prop.PropertyType));
                        break;
                    }
                }
                if (!found)
                {
                    throw new ArgumentException("Unrecognized argument " + arg);
                }
            }
            FileName = Path.GetFileNameWithoutExtension(FileName) + "." + FileFormat.ToString();
        }

        public void PrintUsage()
        {
            Console.Write("Usage: {0} ", Path.GetFileName(System.AppDomain.CurrentDomain.FriendlyName));
            foreach (PropertyInfo prop in GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Console.Write("{0}={1}", prop.Name, prop.GetValue(this));
            }
            Console.WriteLine();
        }

        public string Url { get; private set; } = "https://www.digitalruby.com";
        public string FileName { get; private set; }
        public int Width { get; private set; } = 1280;
        public int Height { get; private set; } = 1024;
        public int LoadDelayMilliseconds { get; private set; } = 1000;
        public int ForceDelayMilliseconds { get; private set; } = 0;
        public float Percent { get; private set; } = 0.1f;
        public int LoopDelaySeconds { get; private set; } = 300;
        public ScreenshotImageFormat FileFormat { get; private set; } = ScreenshotImageFormat.Png;

        public bool EmailTestOnly { get; private set; }
        public string EmailHost { get; private set; }
        public int EmailPort { get; private set; }
        public string EmailUserName { get; private set; }
        public string EmailPassword { get; private set; }
        public string EmailFromAddress { get; private set; }
        public string EmailFromName { get; private set; } = "SnapshotDiff";
        public string EmailToAddress { get; private set; }
        public string EmailSubject { get; private set; } = "Url changed! {0}";
    }

    public class SnapshotDiffApp
    {
        private readonly SnapshotDiffOptions options;
        private readonly CancellationTokenSource cancelToken = new CancellationTokenSource();
        
        private void SendNotificationEmail(byte[] image)
        {
            SmtpClient client = new SmtpClient();
            BodyBuilder builder = new BodyBuilder
            {
                TextBody = "Url changed: " + options.Url + "\n"
            };
            builder.Attachments.Add(options.FileName, image, new ContentType("image", "png"));
            client.Connect(options.EmailHost, options.EmailPort, MailKit.Security.SecureSocketOptions.Auto);
            client.Authenticate(options.EmailUserName, options.EmailPassword);
            client.Send(new MimeMessage(new MailboxAddress[] { new MailboxAddress(options.EmailFromName, options.EmailFromAddress) },
                new MailboxAddress[] { new MailboxAddress(options.EmailToAddress) },
                string.Format(options.EmailSubject, options.Url), builder.ToMessageBody()));
        }

        private int RunInternal()
        {
            if (options.EmailTestOnly)
            {
                SendNotificationEmail(new byte[0]);
                return 0;
            }
            Console.WriteLine("Press Ctrl-C to terminate");
            Console.CancelKeyPress += Console_CancelKeyPress;
            ChromeOptions chromeOptions = new ChromeOptions();
            ChromeDriverService service = ChromeDriverService.CreateDefaultService(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));
            service.SuppressInitialDiagnosticInformation = true;
            service.HideCommandPromptWindow = true;
            chromeOptions.AddArgument("headless");
            chromeOptions.AddArgument("disable-gpu");
            chromeOptions.AddArgument("hide-scrollbars");
            using (var driver = new ChromeDriver(service, chromeOptions))
            {
                driver.Manage().Window.Size = new System.Drawing.Size(options.Width, options.Height);
                driver.Manage().Timeouts().ImplicitWait = TimeSpan.FromMilliseconds(options.LoadDelayMilliseconds);
                while (!cancelToken.IsCancellationRequested)
                {
                    Console.Write("Pinging url {0}... ", options.Url);
                    try
                    {
                        driver.Navigate().GoToUrl(options.Url);
                        Task.Delay(options.ForceDelayMilliseconds).Wait(cancelToken.Token);
                        var screenshot = driver.GetScreenshot();
                        string tempFile = Path.Combine(Path.GetTempPath(), "SnapshotDiffTemp.img");
                        byte[] rawBytes = screenshot.AsByteArray;
                        screenshot.SaveAsFile(tempFile, options.FileFormat);
                        if (File.Exists(options.FileName))
                        {
                            var imgCurrent = Image.Load<Rgba32>(options.FileName);
                            var imgNext = Image.Load<Rgba32>(rawBytes);
                            int pixelsDiff = 0;
                            if (imgCurrent.Width == imgNext.Width && imgCurrent.Height == imgNext.Height)
                            {
                                for (int y = 0; y < imgCurrent.Height; y++)
                                {
                                    for (int x = 0; x < imgCurrent.Width; x++)
                                    {
                                        if (imgCurrent.Frames[0][x, y] != imgNext.Frames[0][x, y])
                                        {
                                            pixelsDiff++;
                                        }
                                    }
                                }
                            }
                            float percentDiff = (float)pixelsDiff / ((float)imgNext.Width * (float)imgNext.Height);
                            if (percentDiff < options.Percent)
                            {
                                Console.WriteLine("No change.");
                            }
                            else
                            {
                                Console.WriteLine("Url changed!");
                                Task.Run(() =>
                                {
                                    try
                                    {
                                        SendNotificationEmail(rawBytes);
                                    }
                                    catch (Exception e)
                                    {
                                        Console.WriteLine("Failed to send notification email: {0}", e);
                                    }
                                });
                            }
                        }
                        else
                        {
                            Console.WriteLine("First ping, saved image.");
                        }
                        File.Delete(options.FileName);
                        File.Move(tempFile, options.FileName);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Error: {0}", ex);
                    }
                    try
                    {
                        Task.Delay(TimeSpan.FromSeconds(options.LoopDelaySeconds)).Wait(cancelToken.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
            return 0;
        }

        public SnapshotDiffApp(SnapshotDiffOptions options)
        {
            this.options = options;
        }

        public Task<int> Run()
        {
            return Task.Run(() => RunInternal());
        }

        private void Console_CancelKeyPress(object sender, ConsoleCancelEventArgs e)
        {
            cancelToken.Cancel();
        }

        public static async Task<int> Main(string[] args)
        {
            if (args.Length == 0 || args[0].Contains("help", StringComparison.OrdinalIgnoreCase))
            {
                new SnapshotDiffOptions(null).PrintUsage();
                return 1;
            }
            return await new SnapshotDiffApp(new SnapshotDiffOptions(args)).Run();
        }
    }
}
