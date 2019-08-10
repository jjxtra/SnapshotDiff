using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using MailKit;
using MailKit.Net;
using MailKit.Net.Smtp;
using MimeKit;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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

        public SnapshotDiffOptions(JToken args, int id)
        {
            if (args == null)
            {
                return;
            }
            Id = id;
            foreach (JProperty arg in args)
            {
                string key = arg.Name.Trim();
                string value = arg.Value.ToString().Trim();
                bool found = false;
                foreach (PropertyInfo prop in GetProps())
                {
                    if (prop.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        if (prop.PropertyType.IsEnum)
                        {
                            prop.SetValue(this, Enum.Parse(prop.PropertyType, value, true));
                        }
                        else
                        {
                            prop.SetValue(this, Convert.ChangeType(value, prop.PropertyType, CultureInfo.InvariantCulture));
                        }
                        break;
                    }
                }
                if (!found)
                {
                    throw new ArgumentException("Unrecognized argument " + key);
                }
            }
            if (EmailHost == "youremailserver")
            {
                throw new ArgumentException("Invalid email server " + EmailHost);
            }
            EmailFromAddresses = new MailboxAddress[] { new MailboxAddress(EmailFromName, EmailFromAddress) };
            EmailToAddresses = EmailToAddress.Split(',').Select(e => new MailboxAddress(e)).ToArray();
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
            Console.WriteLine("[{");
            PropertyInfo[] props = GetProps();
            for (int i = 0; i < props.Length; i++)
            {
                PropertyInfo prop = props[i];
                Console.WriteLine("  \"{0}\": \"{1}\"{2}", prop.Name, prop.GetValue(this), (i < props.Length - 1 ? "," : string.Empty));
            }
            Console.WriteLine("}]");
        }

        // url/image properties
        public string Url { get; private set; } = "https://www.digitalruby.com";
        public string FileName { get; private set; } = "SnapshotDiff.png";
        public int BrowserWidth { get; private set; } = 1280;
        public int BrowserHeight { get; private set; } = 1024;
        public string Rect { get; private set; } = "0,0,0,0";
        public int TimeoutMilliseconds { get; private set; } = 60000;
        public float Percent { get; private set; } = 0.1f;
        public int LoopDelaySeconds { get; private set; } = 300;

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
        public int Id { get; }
    }

    public class SnapshotDiffInstance : IDisposable
    {
        private readonly SnapshotDiffOptions options;
        private readonly CancellationTokenSource cancelToken = new CancellationTokenSource();
        private readonly string workingDir;
        private readonly string processToRun;
        private readonly string processArgs;

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

        // google-chrome-stable --headless --disable-gpu --disable-features=NetworkService --virtual-time-budget=60000 --window-size=1280,1024 --hide-scrollbars --screenshot=screen1.png https://www.kimsufi.com/us/en/order/kimsufi.xml?reference=1804sk20
        private async Task<int> RunInternal()
        {
            if (options.EmailTestOnly)
            {
                SendNotificationEmail(new byte[0]);
                return 0;
            }

            Console.CancelKeyPress += Console_CancelKeyPress;
            float percentMultiplier = (1.0f / ((float)options.BrowserWidth * (float)options.BrowserHeight));
            TimeSpan delaySeconds = TimeSpan.FromSeconds(options.LoopDelaySeconds);
            string existingFile = Path.Combine(workingDir, options.FileName);
            string tempFile = existingFile + ".tmp";
            await File.WriteAllLinesAsync(Path.Combine(workingDir, "info.txt"), new string[]
            {
                "Url=" + options.Url
            });
            using (StreamWriter logFile = File.CreateText(Path.Combine(workingDir, "log.txt")))
            {
                while (!cancelToken.IsCancellationRequested)
                {
                    logFile.WriteLine("Pinging url {0}... ", options.Url);
                    try
                    {
                        if (File.Exists(tempFile))
                        {
                            File.Delete(tempFile);
                        }
                        float percentDiff = 0.0f;
                        string processArgsFormatted = string.Format(processArgs, existingFile, options.Url, options.TimeoutMilliseconds, options.BrowserWidth, options.BrowserHeight);
                        using (Process p = Process.Start(processToRun, processArgsFormatted))
                        {
                            p.WaitForExit(options.TimeoutMilliseconds);
                            if (!p.HasExited)
                            {
                                await logFile.WriteLineAsync("Screenshot timed out");
                                try
                                {
                                    p.Kill();
                                }
                                catch
                                {
                                }
                            }
                        }
                        byte[] rawBytes = await File.ReadAllBytesAsync(tempFile);
                        if (File.Exists(existingFile))
                        {
                            var imgCurrent = Image.Load<Rgba32>(existingFile);
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
                            percentDiff = (float)pixelDifferentCount * percentMultiplier;
                            await logFile.WriteLineAsync(string.Format("{0:0.00} percent different.", percentDiff * 100.0f));
                            if (percentDiff >= options.Percent)
                            {
                                await logFile.WriteLineAsync("Url percent difference threshold exceeded!");
                                Task.Run(async () =>
                                {
                                    try
                                    {
                                        SendNotificationEmail(rawBytes);
                                    }
                                    catch (Exception e)
                                    {
                                        await logFile.WriteLineAsync("Failed to send notification email: " + e);
                                    }
                                }).GetAwaiter();
                            }
                        }
                        else
                        {
                            await logFile.WriteLineAsync("First ping, saved image.");
                        }
                        if (percentDiff > 0.0f)
                        {
                            if (File.Exists(existingFile))
                            {
                                File.Delete(existingFile);
                            }
                            if (File.Exists(tempFile))
                            {
                                File.Move(tempFile, existingFile);
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        await logFile.WriteLineAsync("Error: " + ex);
                    }
                    try
                    {
                        await Task.Delay(delaySeconds, cancelToken.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
            return 0;
        }

        public SnapshotDiffInstance(SnapshotDiffOptions options, string path, string processToRun, string processArgs)
        {
            this.options = options;
            this.workingDir = path;
            this.processToRun = processToRun;
            this.processArgs = processArgs;
            Directory.CreateDirectory(path);
        }

        public void Dispose()
        {
        }

        public async Task<int> Run()
        {
            using (this)
            {
                return await RunInternal();
            }
        }

        private void Console_CancelKeyPress(object sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            cancelToken.Cancel();
        }

        public static async Task<int> Main(string[] args)
        {
            if (args.Length == 0 || args[0].Contains("help", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Pass the json file with your commands as the first argument. Optional path to work in as second argument.");
                Console.WriteLine("The json file may contain:");
                new SnapshotDiffOptions(null, 0).PrintUsage();
                return 1;
            }
            Console.WriteLine("Setting up web browser. Press Ctrl-C to terminate.");
            List<Task> tasks = new List<Task>();
            int id = 0;
            using (StreamReader file = File.OpenText(args[0]))
            using (JsonTextReader reader = new JsonTextReader(file))
            {
                JToken token = await JToken.LoadAsync(reader);
                string path = (args.Length == 1 ? Directory.GetCurrentDirectory() : Path.GetFullPath(args[1]));
                JToken process = token["process"];
                JToken commands = token["commands"];

                string processToRun = process["processToRun"].ToString();
                string processArgs = process["processArgs"].ToString();

                string toKill = Path.GetFileNameWithoutExtension(processToRun);
                foreach (Process p in Process.GetProcessesByName(toKill))
                {
                    try
                    {
                        p.Kill();
                    }
                    catch
                    {
                    }
                }

                // Load each object from the stream and do something with it
                foreach (JToken obj in commands)
                {
                    int nextId = ++id;
                    Console.WriteLine("Starting url listener for {0}...", obj["Url"]);
                    string subPath = Path.Combine(path, nextId.ToString());
                    SnapshotDiffInstance inst = new SnapshotDiffInstance(new SnapshotDiffOptions(obj, nextId), subPath, processToRun, processArgs);
                    tasks.Add(Task.Run(() => inst.Run()));
                }
            }
            await Task.WhenAll(tasks);
            return 0;
        }
    }
}
