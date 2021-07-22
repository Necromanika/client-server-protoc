﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Linq;
using System.IO;
using ProtoBuf;

namespace serverConsoleApp
{
    class Program
    {
        const int port = 13031; // порт для прослушивания подключений
        static TimerCallback tcb = new TimerCallback(CurrentTime);
        static Timer timer;
        static Queue<byte[]> messageQ = new Queue<byte[]>(); // очередь сообщений
        static object locker = new object();
        static int clients=0; // четчик клиентов
        static AutoResetEvent waitHandler = new AutoResetEvent(false); // событие-сигнал
        static volatile bool writeOn = false;
        static void Main(string[] args)
        {
            TcpListener server = null; // сервер
            try
            {
                IPAddress localAddr = IPAddress.Parse("127.0.0.1");
                server = new TcpListener(localAddr, port); // запуск сервера
                server.Start();

                while (true)
                {
                    TcpClient client = server.AcceptTcpClient();// получаем входящее подключение
                    Thread clientConn = new Thread(() => Connection(client)); // создаем новый поток
                    clientConn.Start();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
            finally
            {
                if(server!=null)
                    server.Stop(); // останавливаем
            }
        }

        private static void AddInQ(byte[] message)
        {
            lock (locker)
            {
                messageQ.Enqueue(message); // добавляем сообщение в очередь
            }
            waitHandler.Set(); // сигнал
        }

        private static byte[] SerializeMessage(CommonMessage mess)
        {
            using (var ms = new MemoryStream())
            {
                Serializer.Serialize(ms, mess); // сериализуем сообщение
                return ms.ToArray();
            }
        }

        private static CommonMessage DeserializeMessage(byte[] mess, int size)
        {
            mess = mess.Take(size).ToArray();
            using (var ms = new MemoryStream(mess))
            {
                return Serializer.Deserialize<CommonMessage>(ms); // десериализуем сообщение
            }
        }

        private static void CurrentTime(object obj)
        {
            
            CommonMessage mess = new CommonMessage()// сообщения для отправки
            {
                Ctrl = 1,
                Text = DateTime.Now.TimeOfDay.ToString("hh\\:mm\\:ss")
            };
            AddInQ(SerializeMessage(mess)); // добавляем в очередь сообщеие

            //byte[] time = Encoding.UTF8.GetBytes(DateTime.Now.TimeOfDay.ToString("hh\\:mm\\:ss"));
            //byte[] mess = new byte[] { 0x01, Convert.ToByte(time.Length) }.Concat(time).ToArray();
            //AddInQ(mess);
        }
        private static void Connection(TcpClient client)
        {
            int clientNumber = Interlocked.Increment(ref clients);
            Console.WriteLine("Подключен клиент № {0}", clientNumber); // логирунм
            
            using (NetworkStream stream = client.GetStream())// получаем поток для чтения и записи
            {
                Thread writeThread = new Thread(() => WriteData(stream)); // создаем новый поток отправки
                writeOn = true;
                writeThread.Start();
                timer = new Timer(tcb, stream, 0, 10000);
                byte[] data = new byte[255];
                int size;
                string read;
                byte[] mess;
                byte[] dataResponse;
                CommonMessage recievedMessage;
                try
                {
                    while (true)
                    {
                        //stream.ReadAll(data, 0, 1);// читаем управляющий бит
                        //byte control = data[0];
                        //stream.ReadAll(data, 0, 255); // читаем что доступно
                        int readBytes = stream.Read(data, 0, data.Length); // читаем что доступно
                        recievedMessage = DeserializeMessage(data, readBytes); //десериализуем
                        int control = recievedMessage.Ctrl;
                        string readText = recievedMessage.Text;

                        if (control == 0) // если пришло сообщение об отключении клиента (0x0f) отключаем его
                        {
                            break;
                        }
                        if (control == 1)
                        {
                            Console.WriteLine("Клиент № {0}: {1}", clientNumber, readText); // логируем
                            AddInQ(SerializeMessage(recievedMessage)); // добавляем в очередь сообщеие
                        }
                        /* if (control == 0x01)
                         {
                             stream.ReadAll(data, 0, 1); //читаем размер сообщения
                             size = Convert.ToInt32(data[0]);
                             stream.ReadAll(data, 0, size); // читаем сообщение
                             read = Encoding.UTF8.GetString(data, 0, size);

                             Console.WriteLine("Клиент № {0}: {1}", clientNumber, read); // логируем
                             dataResponse = Encoding.UTF8.GetBytes(read);
                             mess = new byte[] { 0x01, Convert.ToByte(dataResponse.Length) }.Concat(dataResponse).ToArray(); // сообщение на отправку 1байт - управляющий байт, 2байт - размер
                             AddInQ(mess); // ставим в очередь
                         }*/
                    }
                }
                catch(Exception ex)
                {
                    Console.WriteLine(ex.Message);
                }
                finally
                {
                    writeOn = false;
                    waitHandler.Set();
                    timer.Dispose();
                    stream.Close();// закрываем поток
                    client.Close();// закрываем подключение
                    Console.WriteLine("Клиент № {0} отключен.", clientNumber); // логируем
                }
            }
        }

        private static void WriteData(NetworkStream ns)
        {
            do
            {
                waitHandler.WaitOne();
                lock (locker)
                {
                    while (messageQ.Count > 0) // если есть сообщения в очереди
                    {
                        byte[] mess = messageQ.Dequeue();
                        ns.Write(mess, 0, mess.Length); // пишем в сокет
                    }
                }
            } while (writeOn);
        }
    }

    static class StreamExtension
    {
        public static int ReadAll(this NetworkStream stream, byte[] buffer, int offset, int size)
        {
            int bytes;
            int bytesRead = 0;
            do
            {
                bytes = stream.Read(buffer, offset + bytesRead, size - bytesRead);
                if (bytes == 0)
                    throw new IOException();
                bytesRead += bytes;
            } while (bytesRead < size);
            return bytesRead;
        }
    }
}
