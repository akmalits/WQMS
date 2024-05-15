﻿using System;
using System.IO;
using System.Net;
using System.IO.Ports;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Yieryi {
    class Program {

        enum ResponseFormat {
            Ph,
            Orp
        }

        private const string USB_PORT = "/dev/cu.usbserial-130"; // The COM port that the USB cable is connected to
        private const string UDP_IP = "192.168.50.115"; // The IP that is printed in the serial monitor from the ESP32
        private const int SHARED_UDP_PORT = 4210; // UDP port that the ESP32 is listening on

        static double cf = 0;
        static double ec = 0;
        static double tds = 0;
        static double ph = 0; 
        static double orp = 0;
        static double re = 0;
        static double temp = 0;

        static readonly object dataLock = new object();

        static ushort CalculateModbusCrc(byte[] data, int offset, int count) {
            ushort crc = 0xFFFF;

            for (int i = 0; i + offset < data.Length && i < count; i++) {
                crc ^= data[i + offset];

                for (int j = 8; j > 0; j--) {
                    if ((crc & 0x0001) != 0) {
                        crc >>= 1;
                        crc ^= 0xA001;
                    } else {
                        crc >>= 1;
                    }
                }
            }
            return crc;
        }

        static void VerifyModbusCrc(byte[] data) {
            ushort crc = (ushort)((data[data.Length - 1] << 8) | data[data.Length - 2]);
            var calculatedCrc = CalculateModbusCrc(data, 0, data.Length - 2);
            if (crc != calculatedCrc) {
                throw new ArgumentOutOfRangeException($"CRC 0x{calculatedCrc:X4} does not match 0x{crc:X4}");
            }
        }

        static byte[] AppendModbusCrc(byte[] data) {
            byte[] crcData = new byte[data.Length + 2];
            Array.Copy(data, crcData, data.Length);
            ushort crc = CalculateModbusCrc(data, 0, data.Length);
            crcData[crcData.Length - 2] = (byte)(crc & 0xFF);
            crcData[crcData.Length - 1] = (byte)((crc >> 8) & 0xFF);
            return crcData;
        }

        static byte[] ReadBytes(SerialPort serialPort, int count) {
            byte[] data = new byte[count];
            int bytesRead = 0;
            while (bytesRead < count) {
                int readCount = serialPort.Read(data, bytesRead, count - bytesRead);
                if (readCount > 0) {
                    bytesRead += readCount;
                }
            }
            return data;
        }

        static byte[] IssueReadData(SerialPort serialPort, byte address) {
            byte[] readCommand = AppendModbusCrc(new byte[] {
                address, 0x03, 0x00, 0x00, 0x00, 0x04 });
            VerifyModbusCrc(readCommand);
            serialPort.DiscardInBuffer();
            serialPort.Write(readCommand, 0, readCommand.Length);
            var response = ReadBytes(serialPort, 16);
            VerifyModbusCrc(response);

            return response;
        }

        static void IssueSetResponseFormat(SerialPort serialPort, byte address, ResponseFormat format) {
            byte[] setOrpFormatCommand = AppendModbusCrc(new byte[] {
                address, 0x06, 0x00, 0x05, 0x00, (byte)(format == ResponseFormat.Orp ? 0x01 : 0x00) });
            VerifyModbusCrc(setOrpFormatCommand);

            serialPort.DiscardInBuffer();
            serialPort.Write(setOrpFormatCommand, 0, setOrpFormatCommand.Length);
        }

        private static void SendDataToESP32(double cf, double ec, double tds, double ph, double orp, double re, double temp) {
            using (UdpClient client = new UdpClient()) {
                IPEndPoint endPoint = new IPEndPoint(IPAddress.Parse(UDP_IP), SHARED_UDP_PORT);

                string messageToSend = $"EC: {ec:0.00} mS/cm | TDS: {tds:0.00} ppm | CF: {cf:0.00} CF | pH: {ph:0.00} pH | ORP: {orp} mV | Humidity(Air): {re} % | Temp: {temp:0.0} °C";
                byte[] dataToSend = Encoding.UTF8.GetBytes(messageToSend);
                client.Send(dataToSend, dataToSend.Length, endPoint);
                Console.WriteLine("Data sent to Running Text: " + messageToSend);
            }
        }

        private static void GetData() {
            string portName = USB_PORT;
            int baudRate = 9600;
            Parity parity = Parity.None;
            StopBits stopBits = StopBits.One;
            byte address = 0x01;
            ResponseFormat responseFormat = ResponseFormat.Ph;
            bool switchBetweenResponseFormats = true;

            using (SerialPort serialPort = new SerialPort(portName, baudRate, parity, 8, stopBits)) {
                try {
                    serialPort.ReadTimeout = 1000;
                    serialPort.Open();

                    IssueSetResponseFormat(serialPort, address, responseFormat);
                    Thread.Sleep(1000); // longer delay otherwise orp <-> ph values sometimes are mixed

                    for (int t = 0; t < 10000; t++) {
                        try {
                            var response = IssueReadData(serialPort, address);

                            lock (dataLock) {
                                cf = ((response[4] << 8) | response[5]) / 100.0;
                                ec = cf / 10.0;
                                tds = cf * 50.0;
                                re = ((response[8] << 8) | response[9]);
                                temp = ((response[10] << 8) | response[11]) / 10.0;

                                if (responseFormat == ResponseFormat.Ph) {
                                    ph = ((response[6] << 8) | response[7]) / 100.0;
                                } else {
                                    orp = ((response[6] & 0x40) == 0 ? 1 : -1) * (((response[6] & 0x3F) << 8) | response[7]);
                                }
                            }

                            Thread.Sleep(200); // wait 200ms before next request to allow device to recover.

                            if (switchBetweenResponseFormats) {
                                responseFormat = responseFormat == ResponseFormat.Orp ? ResponseFormat.Ph : ResponseFormat.Orp;
                                IssueSetResponseFormat(serialPort, address, responseFormat);
                                Thread.Sleep(800); // longer delay otherwise orp <-> ph values sometimes are mixed
                            }

                            lock (dataLock) {
                                Console.WriteLine($"[{DateTime.Now:dd-MM-yyyy HH:mm:ss}] cf: {cf:0.00}, ec: {ec:0.00} mS, tds: {tds:0.00} ppm, ph: {ph:0.00} pH, orp: {orp} mV, re: {re} %, temp: {temp:0.0} °C");
                                SaveCSVFile(cf, ec, tds, ph, orp, re, temp); 
                            }
                        } catch (Exception ex) {
                            Console.WriteLine($"Error: {ex.Message}");
                        }
                    }
                } catch (Exception ex) {
                    Console.WriteLine("Error: " + ex.Message);
                }
            }
        }

        private static void SaveCSVFile(double cf, double ec, double tds, double ph, double orp, double re, double temp) {
            string csvFilePath = "data.csv";
            string timestamp = DateTime.Now.ToString("dd-MM-yyyy HH:mm:ss");
            string csvContent = $"{timestamp},{cf:0.00},{ec:0.00},{tds:0.00},{ph:0.00},{orp},{re},{temp:0.0}\n";

            try {
                if (!File.Exists(csvFilePath)) {
                    string header = "Timestamp,CF,EC,TDS,pH,ORP,RE,Temp\n";
                    File.WriteAllText(csvFilePath, header);
                }

                File.AppendAllText(csvFilePath, csvContent);
                Console.WriteLine("Data saved to CSV file.");
            } catch (Exception ex) {
                Console.WriteLine($"Error saving data to CSV file: {ex.Message}");
            }
        }

        private static void GenerateRandomNumbers() {
            Random random = new Random();
            while (true) {
                lock (dataLock) {
                    cf = random.NextDouble() * 100;
                    ec = random.NextDouble() * 100;
                    tds = random.NextDouble() * 100;
                    ph = random.NextDouble() * 100;
                    orp = random.NextDouble() * 100;
                    re = random.NextDouble() * 100;
                    temp = random.NextDouble() * 100;
                    Console.WriteLine($"[{DateTime.Now:dd-MM-yyyy HH:mm:ss}] cf: {cf:0.00}, ec: {ec:0.00} mS, tds: {tds:0.00} ppm, ph: {ph:0.00} pH, orp: {orp} mV, re: {re} %, temp: {temp:0.0} °C");
                    SaveCSVFile(cf, ec, tds, ph, orp, re, temp); 
                }
                Thread.Sleep(1000); 
            }
        }

        static void Main(string[] args) {
            // Thread getDataThread = new Thread(GetData);
            // getDataThread.Start();

            Thread getRandomNumber = new Thread(GenerateRandomNumbers);
            getRandomNumber.Start();

            while (true) {
                lock (dataLock) {
                    SendDataToESP32(cf, ec, tds, ph, orp, re, temp);
                }
                Thread.Sleep(5000);
            }
        }
    }
}