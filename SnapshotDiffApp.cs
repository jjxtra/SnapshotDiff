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
using SixLabors.ImageSharp.Processing;
using SixLabors.Primitives;

namespace SnapshotDiff
{
    public class SnapshotDiffOptions
    {
        private PropertyInfo[] GetProps()
        {
            return GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.SetProperty).Where(p => p.CanWrite).ToArray();
        }

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
                foreach (PropertyInfo prop in GetProps())
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
            if (EmailHost == "youremailserver")
            {
                throw new ArgumentException("Invalid email server " + EmailHost);
            }
            EmailFromAddresses = new MailboxAddress[] { new MailboxAddress(EmailFromName, EmailFromAddress) };
            EmailToAddresses = EmailToAddress.Split(',').Select(e => new MailboxAddress(e)).ToArray();
            FileName = Path.GetFileNameWithoutExtension(FileName) + "." + FileFormat.ToString();
            string[] rectPieces = Rect.Split(',');
            try
            {
                Rectangle srcRect = new Rectangle(int.Parse(rectPieces[0]), int.Parse(rectPieces[1]), int.Parse(rectPieces[2]), int.Parse(rectPieces[3]));
                if (srcRect.X >= BrowserWidth)
                {
                    throw new ArgumentException("Rect is out of bounds from browser width");
                }
                if (srcRect.Y >= BrowserHeight)
                {
                    throw new ArgumentException("Rect is out of bounds from browser height");
                }
                if (srcRect.Width <= 0 || srcRect.X + srcRect.Width > BrowserWidth)
                {
                    srcRect.Width = BrowserWidth - srcRect.X;
                }
                if (srcRect.Height <= 0 || srcRect.Y + srcRect.Height > BrowserHeight)
                {
                    srcRect.Height = BrowserHeight - srcRect.Y;
                }
                SourceRect = srcRect;
            }
            catch (Exception ex)
            {
                throw new ArgumentException("Invalid Rect: " + Rect + ", " + ex);
            }
        }

        public void PrintUsage()
        {
            Console.Write("Usage: {0} ", Path.GetFileName(System.AppDomain.CurrentDomain.FriendlyName));
            foreach (PropertyInfo prop in GetProps())
            {
                Console.Write("\"{0}={1}\" ", prop.Name, prop.GetValue(this));
            }
            Console.WriteLine();
        }

        // url/image properties
        public string Url { get; private set; } = "https://www.digitalruby.com";
        public string FileName { get; private set; } = "SnapshotDiff.png";
        public int BrowserWidth { get; private set; } = 1280;
        public int BrowserHeight { get; private set; } = 1024;
        public string Rect { get; private set; } = "0,0,0,0";
        public int LoadDelayMilliseconds { get; private set; } = 1000;
        public int ForceDelayMilliseconds { get; private set; } = 0;
        public float Percent { get; private set; } = 0.1f;
        public int LoopDelaySeconds { get; private set; } = 300;
        public ScreenshotImageFormat FileFormat { get; private set; } = ScreenshotImageFormat.Png;

        // notification properties
        public bool EmailTestOnly { get; private set; }
        public string EmailHost { get; private set; } = "youremailserver";
        public int EmailPort { get; private set; } = 25;
        public string EmailUserName { get; private set; } = "youremailusername";
        public string EmailPassword { get; private set; } = "youremailpassword";
        public string EmailFromAddress { get; private set; } = "youremailfromaddress";
        public string EmailFromName { get; private set; } = "SnapshotDiff";
        public string EmailToAddress { get; private set; } = "emailaddress1,emailaddress2";
        public string EmailSubject { get; private set; } = "Url changed! {0}";

        // computed properties
        public Rectangle SourceRect { get; }
        public MailboxAddress[] EmailFromAddresses { get; }
        public MailboxAddress[] EmailToAddresses { get; }
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
            client.Send(new MimeMessage(options.EmailFromAddresses, options.EmailToAddresses,
                string.Format(options.EmailSubject, options.Url), builder.ToMessageBody()));
        }

        private int RunInternal()
        {
            if (options.EmailTestOnly)
            {
                SendNotificationEmail(new byte[0]);
                return 0;
            }
            Console.WriteLine("Setting up web browser. Press Ctrl-C to terminate.");
            Console.CancelKeyPress += Console_CancelKeyPress;
            string tempFile = Path.Combine(Path.GetTempPath(), "SnapshotDiffTemp.img");
            float percentMultiplier = (1.0f / ((float)options.BrowserWidth * (float)options.BrowserHeight));
            ChromeOptions chromeOptions = new ChromeOptions();
            ChromeDriverService service = ChromeDriverService.CreateDefaultService(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));
            service.SuppressInitialDiagnosticInformation = true;
            service.HideCommandPromptWindow = true;
            chromeOptions.AddArgument("headless");
            chromeOptions.AddArgument("disable-gpu");
            chromeOptions.AddArgument("hide-scrollbars");
            using (var driver = new ChromeDriver(service, chromeOptions))
            {
                driver.Manage().Window.Size = new System.Drawing.Size(options.BrowserWidth, options.BrowserHeight);
                driver.Manage().Timeouts().ImplicitWait = TimeSpan.FromMilliseconds(options.LoadDelayMilliseconds);
                while (!cancelToken.IsCancellationRequested)
                {
                    Console.Write("Pinging url {0}... ", options.Url);
                    try
                    {
                        driver.Navigate().GoToUrl(options.Url);
                        Task.Delay(options.ForceDelayMilliseconds).Wait(cancelToken.Token);
                        var screenshot = driver.GetScreenshot();
                        byte[] rawBytes = screenshot.AsByteArray;
                        screenshot.SaveAsFile(tempFile, options.FileFormat);
                        if (File.Exists(options.FileName))
                        {
                            var imgCurrent = Image.Load<Rgba32>(options.FileName);
                            imgCurrent.Mutate(i => i.Crop(options.SourceRect));
                            var imgNext = Image.Load<Rgba32>(rawBytes);
                            imgNext.Mutate(i => i.Crop(options.SourceRect));
                            int pixelDifferentCount = 0;
                            if (imgCurrent.Width == imgNext.Width && imgCurrent.Height == imgNext.Height)
                            {
                                for (int y = 0; y < imgCurrent.Height; y++)
                                {
                                    for (int x = 0; x < imgCurrent.Width; x++)
                                    {
                                        if (imgCurrent.Frames[0][x, y] != imgNext.Frames[0][x, y])
                                        {
                                            pixelDifferentCount++;
                                        }
                                    }
                                }
                            }
                            float percentDiff = (float)pixelDifferentCount * percentMultiplier;
                            Console.WriteLine("{0:0.00} percent different.", percentDiff * 100.0f);
                            if (percentDiff >= options.Percent)
                            {
                                Console.WriteLine("Url percent difference threshold exceeded!");
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
                        if (File.Exists(options.FileName))
                        {
                            File.Delete(options.FileName);
                        }
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
