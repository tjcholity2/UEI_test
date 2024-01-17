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
    class UEI_test_class
    {
        private Device[] cubeDeviceArray;
        private List<string> deviceIpList = new List<string>();

        private string analogCardResourceString;
        private Session devSession;
        private AnalogScaledReader readerAnalog;

        private int iChannels;
        private double[] valueA;
        private double[,] dataBuffer;

        private AsyncCallback readerCallback;
        private IAsyncResult readerIAsyncResult;
        private static object readerCallbackLocker = new object();
        const int NumScansRead = 100;

        private StreamWriter mCsvStream = null;
        private object printLocker = new object();
        FileStream fs;

        private System.Timers.Timer CubePulseTimer;

        private System.DateTime lastTime = DateTime.Now;

        public UEI_test_class(List<string> ipList)
        {
            write_delta_time_ToFile();
            deviceIpList = ipList;
            cubeDeviceArray = new Device[deviceIpList.Count()];
        }

        public void setupAnalogInput(string resourceString)
        {
            analogCardResourceString = resourceString;
            devSession = new Session();
            devSession.CreateAIChannel(analogCardResourceString, -10, 10, AIChannelInputMode.Differential);
            iChannels = devSession.GetNumberOfChannels();
            devSession.ConfigureTimingForSimpleIO();
            readerAnalog = new AnalogScaledReader(devSession.GetDataStream()); // Create a reader object to read data synchronously.
            valueA = new double[iChannels];
            devSession.Start();
        }

        public void setupAnalogInputBuffered(string resourceString)
        {
            analogCardResourceString = resourceString;
            devSession = new Session();
            devSession.CreateAIChannel(analogCardResourceString, -10, 10, AIChannelInputMode.Differential);
            devSession.ConfigureTimingForBufferedIO(samplePerChannel: 1000, clkSource: TimingClockSource.Internal, rate: 1000, edge: DigitalEdge.Rising, duration: TimingDuration.Continuous);
            devSession.GetTiming().SetTimeout(5000);  // Seconds?
            devSession.GetDataStream().SetNumberOfFrames(numFrames: 1);
            readerAnalog = new AnalogScaledReader(devSession.GetDataStream()); // Create a reader object to read data synchronously.
            iChannels = devSession.GetNumberOfChannels();
            dataBuffer = new double[1100, iChannels];
            readerCallback = new AsyncCallback(ReaderCallback);
            readerIAsyncResult = readerAnalog.BeginReadMultipleScans(100, readerCallback, null);
            devSession.Start();
        }

        public void devSessionStart()
        {
            devSession.Start();
        }

        public void ReadSingleScan()
        {
            double[] valueA = new double[iChannels];
            valueA = readerAnalog.ReadSingleScan();
        }


        /// <summary>
        /// Loop through all devices and start a watchdog for each Cube that triggers a Cube reset if communications are stopped.
        /// </summary>
        public async void startWatchdogs()
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

        private void ReaderCallback(IAsyncResult ar)
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

        private void write_delta_time_ToFile(bool append = true)
        {
            lock (printLocker)
            {
                if (append)
                {
                    fs = new FileStream("Watchdog_delta_time.log", FileMode.Append);
                }
                else
                {
                    fs = new FileStream("Watchdog_delta_time.log", FileMode.Create);
                }

                try
                {
                    using (mCsvStream = new StreamWriter(fs))
                    {
                        if (mCsvStream != null)
                        {
                            double delta = GetDeltaTime();

                            //if (delta > 30000000)
                            //{
                            string timeStr = DateTime.Now.ToString("MM/dd/yyyy HH:mm:ss.fff");
                            mCsvStream.WriteLine(timeStr + ", " + delta.ToString() + Environment.NewLine);
                            //}
                        }
                    }
                }
                finally
                {
                    if (fs != null)
                    {
                        fs.Dispose();
                    }
                }
            }
        }


        private double GetDeltaTime()
        {
            System.DateTime now = DateTime.Now;
            System.TimeSpan dT = now.Subtract(lastTime);
            lastTime = now;
            return dT.TotalSeconds;
        }


        private void SendCubePulse(object sender, System.Timers.ElapsedEventArgs e)
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
            }
            catch (UeiDaqException exception)
            {
                Console.WriteLine("exception in SendCubePulse SetWatchDogCommand: " + exception.Message);
                Console.ReadLine();
            }
        }
    }


    class Program
    {
        static void Main(string[] args)
        {
            List<string> deviceIpList = new List<string>();

            deviceIpList.Add("172.16.113.111");
            deviceIpList.Add("172.16.113.112");
            deviceIpList.Add("172.16.113.113");
            deviceIpList.Add("172.16.113.114");

            UEI_test_class test = new UEI_test_class(deviceIpList);

            string analogCardResourceString = "pdna://172.16.113.113/dev2/ai0:15";

            bool test_without_buffer = false;

            if (test_without_buffer)
            {
                test.setupAnalogInput(analogCardResourceString);
            }
            else
            {
                test.setupAnalogInputBuffered(analogCardResourceString);
            }

            test.startWatchdogs();

            string timeStr = DateTime.Now.ToString("MM/dd/yyyy HH:mm:ss.fff");
            Console.WriteLine(timeStr + ": Started sending watchdog messages to Cubes.");

            //test.devSessionStart();

            timeStr = DateTime.Now.ToString("MM/dd/yyyy HH:mm:ss.fff");
            Console.WriteLine(timeStr + ": Started analog device session.");

            for (int i = 0; i < 100; i++)
            {
                Thread.Sleep(100);

                if (test_without_buffer)
                {
                    test.ReadSingleScan();
                }
            }

            timeStr = DateTime.Now.ToString("MM/dd/yyyy HH:mm:ss.fff");
            Console.WriteLine("\n\n\n" + timeStr + ": test execution finished.  Hit return to exit program");
            Console.ReadLine();
        }
    }
}
