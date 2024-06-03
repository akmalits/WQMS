using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Yieryi
{
    class Program
    {
        #region Enums
        enum ResponseFormat
        {
            Ph,
            Orp
        }
        #endregion

        #region Constants
        private const string USB_PORT = "/dev/ttyUSB0"; // The COM port that the USB cable is connected to
        private const string UDP_IP = "192.168.1.28"; // The IP that is printed in the serial monitor from the ESP32
        private const int SHARED_UDP_PORT = 4210; // UDP port that the ESP32 is listening on
        private const int DataCollectionInterval = 2000; // 2 seconds
        private const int DataCollectionDuration = 60; // 60 seconds
        #endregion

        #region Static Fields
        static double cf = 0;
        static double ec = 0;
        static double tds = 0;
        static double ph = 0;
        static double orp = 0;
        static double re = 0;
        static double temp = 0;

        static readonly object dataLock = new object();

        static List<double> phData = new List<double>();
        static List<double> orpData = new List<double>();
        #endregion

        #region Main Method
        static void Main(string[] args)
        {
            Thread getDataThread = new Thread(GetData);
            getDataThread.Start();

            Thread sendSaveDataThread = new Thread(SendSaveDataLoop);
            sendSaveDataThread.Start();

            Thread sendAdditionalMessageThread = new Thread(SendAdditionalMessageLoop);
            sendAdditionalMessageThread.Start();
        }
        #endregion

        #region Data Collection Methods
        private static void GetData()
        {
            string portName = USB_PORT;
            int baudRate = 9600;
            Parity parity = Parity.None;
            StopBits stopBits = StopBits.One;
            byte address = 0x01;
            ResponseFormat responseFormat = ResponseFormat.Ph;
            bool switchBetweenResponseFormats = true;

            using (SerialPort serialPort = new SerialPort(portName, baudRate, parity, 8, stopBits))
            {
                try
                {
                    serialPort.ReadTimeout = 1000;
                    serialPort.Open();

                    IssueSetResponseFormat(serialPort, address, responseFormat);
                    Thread.Sleep(2000); // longer delay otherwise orp <-> ph values sometimes are mixed

                    for (int t = 0; t < 10000; t++)
                    {
                        try
                        {
                            var response = IssueReadData(serialPort, address);

                            lock (dataLock)
                            {
                                cf = ((response[4] << 8) | response[5]) / 100.0;
                                ec = cf / 10.0;
                                tds = cf * 50.0;
                                re = ((response[8] << 8) | response[9]);
                                temp = ((response[10] << 8) | response[11]) / 10.0;

                                if (responseFormat == ResponseFormat.Ph)
                                {
                                    ph = ((response[6] << 8) | response[7]) / 100.0;

                                    if (ph >= 0 && ph <= 14)
                                    {
                                        phData.Add(ph);
                                        if (phData.Count > DataCollectionDuration)
                                        {
                                            phData.RemoveAt(0);
                                        }
                                    }
                                }
                                else
                                {
                                    orp = ((response[6] & 0x40) == 0 ? 1 : -1) * (((response[6] & 0x3F) << 8) | response[7]);
                                    orpData.Add(orp);
                                    if (orpData.Count > DataCollectionDuration)
                                    {
                                        orpData.RemoveAt(0);
                                    }
                                }
                            }

                            Thread.Sleep(DataCollectionInterval);

                            if (switchBetweenResponseFormats)
                            {
                                responseFormat = responseFormat == ResponseFormat.Orp ? ResponseFormat.Ph : ResponseFormat.Orp;
                                IssueSetResponseFormat(serialPort, address, responseFormat);
                                Thread.Sleep(800);
                            }

                            lock (dataLock)
                            {
                                Console.WriteLine($"[{DateTime.Now:dd-MM-yyyy HH:mm:ss}] cf: {cf:0.00}, ec: {ec:0.00} mS, tds: {tds:0.00} ppm, ph: {ph:0.00} pH, orp: {orp} mV, re: {re} %, temp: {temp:0.0} C");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error: {ex.Message}");
                        }
                    }

                    // Exit the program when t reaches 10000
                    Console.WriteLine("Data collection completed. Exiting the program...");
                    Environment.Exit(0);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Error: " + ex.Message);
                    Environment.Exit(1);
                }
                finally
                {
                    if (serialPort.IsOpen)
                    {
                        serialPort.Close();
                    }
                }
            }
        }
        #endregion

        #region Additional Message Method
        static void SendAdditionalMessageLoop()
        {
            Thread.Sleep(30000);
            while (true)
            {
                SendAdditionalMessage();
                Thread.Sleep(60000); // Send message every 30 sec
            }
        }

        static void SendAdditionalMessage()
        {
            using (UdpClient client = new UdpClient())
            {
                IPEndPoint endPoint = new IPEndPoint(IPAddress.Parse(UDP_IP), SHARED_UDP_PORT);

                string messageToSend = "IPAL DAN TPS LIMBAH B3 PJT I MOJOKERTO";
                byte[] dataToSend = Encoding.UTF8.GetBytes(messageToSend);
                client.Send(dataToSend, dataToSend.Length, endPoint);
                Console.WriteLine("Additional message sent: " + messageToSend);
            }
        }
        #endregion

        #region CRC Methods
        static ushort CalculateModbusCrc(byte[] data, int offset, int count)
        {
            ushort crc = 0xFFFF;

            for (int i = 0; i + offset < data.Length && i < count; i++)
            {
                crc ^= data[i + offset];

                for (int j = 8; j > 0; j--)
                {
                    if ((crc & 0x0001) != 0)
                    {
                        crc >>= 1;
                        crc ^= 0xA001;
                    }
                    else
                    {
                        crc >>= 1;
                    }
                }
            }
            return crc;
        }

        static void VerifyModbusCrc(byte[] data)
        {
            ushort crc = (ushort)((data[data.Length - 1] << 8) | data[data.Length - 2]);
            var calculatedCrc = CalculateModbusCrc(data, 0, data.Length - 2);
            if (crc != calculatedCrc)
            {
                throw new ArgumentOutOfRangeException($"CRC 0x{calculatedCrc:X4} does not match 0x{crc:X4}");
            }
        }

        static byte[] AppendModbusCrc(byte[] data)
        {
            byte[] crcData = new byte[data.Length + 2];
            Array.Copy(data, crcData, data.Length);
            ushort crc = CalculateModbusCrc(data, 0, data.Length);
            crcData[crcData.Length - 2] = (byte)(crc & 0xFF);
            crcData[crcData.Length - 1] = (byte)((crc >> 8) & 0xFF);
            return crcData;
        }
        #endregion

        #region Serial Port Methods
        static byte[] ReadBytes(SerialPort serialPort, int count)
        {
            byte[] data = new byte[count];
            int bytesRead = 0;
            while (bytesRead < count)
            {
                int readCount = serialPort.Read(data, bytesRead, count - bytesRead);
                if (readCount > 0)
                {
                    bytesRead += readCount;
                }
            }
            return data;
        }

        static byte[] IssueReadData(SerialPort serialPort, byte address)
        {
            byte[] readCommand = AppendModbusCrc(new byte[] {
                address, 0x03, 0x00, 0x00, 0x00, 0x04 });
            VerifyModbusCrc(readCommand);
            serialPort.DiscardInBuffer();
            serialPort.Write(readCommand, 0, readCommand.Length);
            var response = ReadBytes(serialPort, 16);
            VerifyModbusCrc(response);

            return response;
        }

        static void IssueSetResponseFormat(SerialPort serialPort, byte address, ResponseFormat format)
        {
            byte[] setOrpFormatCommand = AppendModbusCrc(new byte[] {
                address, 0x06, 0x00, 0x05, 0x00, (byte)(format == ResponseFormat.Orp ? 0x01 : 0x00) });
            VerifyModbusCrc(setOrpFormatCommand);

            serialPort.DiscardInBuffer();
            serialPort.Write(setOrpFormatCommand, 0, setOrpFormatCommand.Length);
        }
        #endregion

        #region Data Processing Methods
        private static double GetAveragePh()
        {
            lock (dataLock)
            {
                return phData.Any() ? phData.Average() : 0.0;
            }
        }

        private static double GetAverageORP()
        {
            lock (dataLock)
            {
                return orpData.Any() ? orpData.Average() : 0.0;
            }
        }

        private static void SendDataToESP32(double cf, double ec, double tds, double ph, double orp, double re, double temp)
        {
            using (UdpClient client = new UdpClient())
            {
                IPEndPoint endPoint = new IPEndPoint(IPAddress.Parse(UDP_IP), SHARED_UDP_PORT);

                string messageToSend = $"EC: {ec:0.00} mS/cm | TDS: {tds:0.00} ppm | CF: {cf:0.00} CF | pH: {ph:0.00} pH | ORP: {orp:0.0} mV | Humidity: {re} % | Temp: {temp:0.0} C";
                byte[] dataToSend = Encoding.UTF8.GetBytes(messageToSend);
                client.Send(dataToSend, dataToSend.Length, endPoint);
                Console.WriteLine("Data sent to Running Text: " + messageToSend);
            }
        }

        static void SendSaveDataLoop()
        {
            while (true)
            {
                lock (dataLock)
                {
                    double averagePh = GetAveragePh();
                    double averageOrp = GetAverageORP();

                    SendDataToESP32(cf, ec, tds, averagePh, averageOrp, re, temp);
                    SaveCSVFile(cf, ec, tds, averagePh, averageOrp, re, temp);
                }
                Thread.Sleep(60000); // Send data every 30 seconds
            }
        }

        private static void SaveCSVFile(double cf, double ec, double tds, double ph, double orp, double re, double temp)
        {
            string csvFilePath = "data.csv";
            string timestamp = DateTime.Now.ToString("dd-MM-yyyy HH:mm:ss");
            string csvContent = $"{timestamp},{cf:0.00},{ec:0.00},{tds:0.00},{ph:0.00},{orp},{re},{temp:0.0}\n";

            try
            {
                long maxFileSizeBytes = 45L * 1024 * 1024 * 1024; // 45 GB
                int linesToRemove = 604800;

                FileInfo fileInfo = new FileInfo(csvFilePath);

                if (!fileInfo.Exists || fileInfo.Length < maxFileSizeBytes)
                {
                    File.AppendAllText(csvFilePath, csvContent);
                    Console.WriteLine("Data saved to CSV file.");
                }
                else
                {
                    List<string> lines = new List<string>();
                    using (StreamReader reader = new StreamReader(csvFilePath))
                    {
                        while (!reader.EndOfStream)
                        {
                            lines.Add(reader.ReadLine());
                        }
                    }

                    if (lines.Count > linesToRemove)
                    {
                        lines.RemoveRange(0, linesToRemove);
                    }

                    lines.Add(csvContent);

                    using (StreamWriter writer = new StreamWriter(csvFilePath))
                    {
                        foreach (string line in lines)
                        {
                            writer.WriteLine(line);
                        }
                    }

                    Console.WriteLine("Data overwritten and appended to CSV file.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving data to CSV file: {ex.Message}");
            }
        }
        #endregion
    }
}
