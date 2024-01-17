using System;
using System.IO;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Linq;
using System.Xml.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UeiDaq;

namespace Test_buffered_analog_input
{

    class Program
    {
        static Device[] cubeDeviceArray;

        static List<string> deviceIpList = new List<string>();

        static StreamWriter mCsvStream = null;

        static string analogCardResourceString;
        static Session devSession;
        static AnalogScaledReader readerAnalog;
        static double[] valueA;
        static double[,] dataBuffer;
        static AsyncCallback readerCallback;
        static IAsyncResult readerIAsyncResult;
        private static object readerCallbackLocker = new object();
        const int NumScansRead = 100;

        static FileStream fs;

        static System.Timers.Timer CubePulseTimer;

        static System.DateTime lastTime = DateTime.Now;

        public struct PingInfo
        {
            public string ipOrName { get; set; }
            public string id { get; set; }

            public PingInfo(string ipOrName, string id)
            {
                this.ipOrName = ipOrName;
                this.id = id;
            }
        }

        static void Main(string[] args)
        {
            fs = new FileStream("Watchdog_delta_time.log", FileMode.Append);
            mCsvStream = new StreamWriter(fs);

            deviceIpList.Add("172.16.113.110");
            deviceIpList.Add("172.16.113.111");
            deviceIpList.Add("172.16.113.112");
            deviceIpList.Add("172.16.113.113");
            deviceIpList.Add("172.16.113.114");

            cubeDeviceArray = new Device[deviceIpList.Count()];

            analogCardResourceString = "pdna://172.16.113.113/dev2/ai0:15";

            bool test_analog = false;

            if (test_analog)
            {
                setupAnalogInput(analogCardResourceString);
            }
            else
            {
                setupAnalogInputBuffered(analogCardResourceString);
            }

            startWatchdogs();

            string timeStr2 = DateTime.Now.ToString("MM/dd/yyyy HH:mm:ss.fff");
            Console.WriteLine(timeStr2 + ": Started sending watchdog messages to Cubes.");

            devSession.Start();

            string timeStr3 = DateTime.Now.ToString("MM/dd/yyyy HH:mm:ss.fff");
            Console.WriteLine(timeStr3 + ": Started analog device session.");

            for (int i = 0; i < 100 ; i++)
            {
                Thread.Sleep(100);
            }

            timeStr3 = DateTime.Now.ToString("MM/dd/yyyy HH:mm:ss.fff");
            Console.WriteLine("\n\n\n" + timeStr3 + ": test execution finished.  Hit return to exit program");
            Console.ReadLine();
        }

        static void setupAnalogInput(string analogCardResourceString)
        {
            devSession = new Session();
            devSession.CreateAIChannel(analogCardResourceString, -10, 10, AIChannelInputMode.Differential);
            int iChannels = devSession.GetNumberOfChannels();
            devSession.ConfigureTimingForSimpleIO();
            readerAnalog = new AnalogScaledReader(devSession.GetDataStream()); // Create a reader object to read data synchronously.
            valueA = new double[iChannels];
        }

        static void setupAnalogInputBuffered(string analogCardResourceString)
        {
            devSession = new Session();
            devSession.CreateAIChannel(analogCardResourceString, -10, 10, AIChannelInputMode.Differential);
            devSession.ConfigureTimingForBufferedIO(samplePerChannel: 1000, clkSource: TimingClockSource.Internal, rate: 1000, edge: DigitalEdge.Rising, duration: TimingDuration.Continuous);
            devSession.GetTiming().SetTimeout(5000);  // Seconds?
            devSession.GetDataStream().SetNumberOfFrames(numFrames: 8);
            readerAnalog = new AnalogScaledReader(devSession.GetDataStream()); // Create a reader object to read data synchronously.
            int iChannels = devSession.GetNumberOfChannels();
            dataBuffer = new double[1100, iChannels];
            readerCallback = new AsyncCallback(ReaderCallback);
            readerIAsyncResult = readerAnalog.BeginReadMultipleScans(100, readerCallback, null);
        }


        /// <summary>
        /// Loop through all devices and start a watchdog for each Cube that triggers a Cube reset if communications are stopped.
        /// </summary>
        static async void startWatchdogs()
        {
            int index = 0;
            bool connected = false;

            try
            {
                // roll through all devices and start the sessions
                foreach (string ipStr in deviceIpList)
                {
                    connected = false;

                    while (!connected)
                    {
                        cubeDeviceArray[index] = DeviceEnumerator.GetDeviceFromResource("pdna://" + ipStr + "/dev14");

                        if (cubeDeviceArray[index] == null)
                        {
                            string timeStr = DateTime.Now.ToString("MM/dd/yyyy HH:mm:ss.fff");
                            Console.WriteLine(timeStr + ": Awaiting UEI device info:" + ipStr);
                            await System.Threading.Tasks.Task.Delay(50);
                        }
                        else
                        {
                            string timeStr = DateTime.Now.ToString("MM/dd/yyyy HH:mm:ss.fff");
                            Console.WriteLine(timeStr + ": Found UEI device info for:" + ipStr + " Connected.");
                            connected = true;
                        }
                    }
                   index++;
                }

                index = 0;

                foreach (string ipStr in deviceIpList)
                {
                    // Enable watchdog, if not cleared within 1 secs, it will reset the cube/rack 
                    //cubeDeviceArray[index].SetWatchDogCommand(WatchDogCommand.EnableClearOnReceive, 1000);
                    cubeDeviceArray[index].SetWatchDogCommand(WatchDogCommand.EnableClearOnReceive, 3000);

                    index++;
                }

                CubePulseTimer = new System.Timers.Timer(300);
                CubePulseTimer.Elapsed += SendCubePulse;
                CubePulseTimer.AutoReset = true;
                CubePulseTimer.Enabled = true;
            }
            catch (UeiDaqException exception)
            {
                Console.WriteLine(exception.Error + " message:" + exception.Message);
                Console.ReadLine();
            }

            string timeStr2 = DateTime.Now.ToString("MM/dd/yyyy HH:mm:ss.fff");
            Console.WriteLine(timeStr2 + ": Completed start of watchdog(s) for UEI Cubes.");
        }

        static void ReaderCallback(IAsyncResult ar)
        {
            lock (readerCallbackLocker)
            {
                try
                {
                    if (devSession != null && devSession.IsRunning())
                    {
                        dataBuffer = readerAnalog.EndReadMultipleScans(ar);

                        readerIAsyncResult = readerAnalog.BeginReadMultipleScans(NumScansRead, readerCallback, null);
                    }
                }
                catch (UeiDaqException exception)
                {
                    string errStr = exception.Error + ". UEI Cube card " + analogCardResourceString;
                    Console.ReadLine();
                }
                catch (Exception e)
                {
                    string errStr = e.Message + ". UEI Cube card " + analogCardResourceString;
                    Console.ReadLine();
                }
            }
        }


        static void write_delta_time_ToFile()
        {
            double delta1 = GetDeltaTime();
            string timeStr = DateTime.Now.ToString("MM/dd/yyyy HH:mm:ss.fff");
            mCsvStream.WriteLine(timeStr + ", " + delta1.ToString() + Environment.NewLine);
        }


        static double GetDeltaTime()
        {
            System.DateTime now = DateTime.Now;
            System.TimeSpan dT = now.Subtract(lastTime);
            lastTime = now;
            return dT.TotalSeconds;
        }


        static void SendCubePulse(object sender, System.Timers.ElapsedEventArgs e)
        {
            try
            {
                // Go through all Cubes and send a watchdog timer reset message.
                for (int i = 0; i < cubeDeviceArray.Count(); i++)
                {
                    //cubeDeviceArray[i].SetWatchDogCommand(WatchDogCommand.Clear, 1000);
                    cubeDeviceArray[i].SetWatchDogCommand(WatchDogCommand.Clear, 3000);
                }

                write_delta_time_ToFile();

                //string timeStr2 = DateTime.Now.ToString("MM/dd/yyyy HH:mm:ss.fff");

                //Console.WriteLine("Finish SendCubePulse loop.");
            }
            catch (UeiDaqException exception)
            {
                Console.WriteLine("exception in SendCubePulse SetWatchDogCommand: " + exception.Message);
                Console.ReadLine();
            }
        }
    }
}
