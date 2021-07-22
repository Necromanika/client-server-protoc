﻿using System;
using System.Text;
using System.Windows.Forms;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Google.Protobuf;
using ProtoBuf;

namespace clientApp
{
    public partial class Form1 : Form
    {
        private const int _port = 13031; // порт
        private const string _server = "127.0.0.1"; // IP
        private bool _conn = false; // подключение есть/нет
        private TcpClient _client; // клиент
        private NetworkStream _stream; // поток
        private Queue<byte[]> _messageQ = new Queue<byte[]>(); // очередь
        private SemaphoreSlim _sem = new SemaphoreSlim(1); // семафор
        private bool _writeOn = false;

        public Form1()
        {
            InitializeComponent();
            textBox2.Enabled = false;
            btn_Enter.Enabled = false;
        }

        private void Btn_connect_Click(object sender, EventArgs e)
        {
            Conn_Disconn();
        }

        private async Task Conn_Disconn()
        {
            if (!_conn) // если не подключены - подключаемся
            {
                _client = new TcpClient();
                try
                {
                    await _client.ConnectAsync(_server, _port);
                    textBox1.AppendText(String.Format("\r\nПодключен к сервверу: {0}:{1}", _server, _port)); // логируем

                    _stream = _client.GetStream(); // получаем поток
                    btn_connect.Text = "Отключиться";
                    _conn = true;
                    textBox2.Enabled = true;
                    btn_Enter.Enabled = true;
                    _writeOn = true;

                    byte[] keepAliveOptions = new byte[0];
                    keepAliveOptions = keepAliveOptions.Concat(BitConverter.GetBytes((uint)1)).ToArray(); // включаем keep-alive
                    keepAliveOptions = keepAliveOptions.Concat(BitConverter.GetBytes((uint)5000)).ToArray(); // время без активности для отправки первой проверки
                    keepAliveOptions = keepAliveOptions.Concat(BitConverter.GetBytes((uint)5000)).ToArray(); // интервал перепроверок
                    _client.Client.IOControl(IOControlCode.KeepAliveValues, keepAliveOptions, null); // передаем настройки для keep-alive

                    Write_Data();
                    await Read_Data();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message);
                }
            }
            else //отключаемся
            {
                _writeOn = false;
                //byte[] data = new byte[] { 0x0f }; // посылаем сигнал о отключении на сервер
                CommonMessage mess = new CommonMessage()// сообщения для отправки
                {
                    Ctrl = 0
                };
                AddInQ(SerializeMessage(mess)); // добавляем в очередь сообщение о отключение
                Discon();
            }
        }

        private void Discon()
        {
            textBox1.AppendText(String.Format("\r\nОтключен от сервера: {0}:{1}", _server, _port)); // логируем
            btn_connect.Text = "Подключиться";
            _conn = false;
            textBox2.Enabled = false;
            btn_Enter.Enabled = false;
        }

        private  void Btn_Enter_Click(object sender, EventArgs e)
        {
            Create_Message();
        }

        private void AddInQ(byte[] message)
        {
            _messageQ.Enqueue(message); // ставим в очередь сообщений
            _sem.Release(); // сигналим
        }

        private byte[] SerializeMessage(CommonMessage mess)
        {
            using (var ms = new MemoryStream())
            {
                Serializer.Serialize(ms, mess); // сериализуем сообщение
                var asd = ms.ToArray();
                return asd;
            }
        }

        private CommonMessage DeserializeMessage(byte[] mess, int size)
        {
            mess = mess.Take(size).ToArray();
            using (var ms = new MemoryStream(mess))
            {
                return Serializer.Deserialize<CommonMessage>(ms); // десериализуем сообщение
            }
        }

        private void Create_Message()
        {
            if (textBox2.Text.Length == 0) // проверка пустой строки
                return;

            CommonMessage mess = new CommonMessage()// сообщения для отправки
            {
                Ctrl = 1,
                Text = textBox2.Text
            };
            
            //mess = new byte[] { 0x01, Convert.ToByte(text.Length) }.Concat(text).ToArray(); // сообщение на отправку 2байт - размер, 1байт - управляющий байт
            AddInQ(SerializeMessage(mess)); // добавляем в очередь сообщеие
            textBox2.Text = "";
            textBox2.Focus();
        }

        private async Task Write_Data()
        {
            byte[] mess;
            try
            {
                do
                {
                    await _sem.WaitAsync();
                    while (_messageQ.Count > 0) // для всех в очереди
                    {
                        mess = _messageQ.Dequeue();
                        await _stream.WriteAsync(mess, 0, mess.Length); // пишем в сокет
                    }
                } while (_writeOn);
                _stream.Close(); // закрываем соединение когда больше не пишем
                _client.Close();
            }
            catch
            {
                await Reconnect();
            }
        }

        private async Task Read_Data()
        {
            try
            {
                while (_conn)
                {
                    byte[] dataResponse = new byte[255];
                    CommonMessage recievedMessage;
                    //int size;
                    //string read;

                    //await _stream.ReadAsyncAll(dataResponse, 0, 1);// читаем управляющий бит
                    //await _stream.ReadAsyncAll(dataResponse, 0, 255); // читаем всё доступное в потоке
                    int gotBytes = await _stream.ReadAsync(dataResponse, 0, dataResponse.Length); // читаем всё доступное в потоке
                    recievedMessage = DeserializeMessage(dataResponse, gotBytes);
                    //byte control = dataResponse[0];

                    if (recievedMessage.Ctrl == 1) // текстовое сообщение
                    {
                        textBox3.AppendText("\n" + recievedMessage.Text); // обновляем UI
                    }
                    /*if (control == 0x01) // текстовое сообщение
                    {
                        await _stream.ReadAsyncAll(dataResponse, 0, 1); //читаем размер сообщения
                        size = dataResponse[0];
                        await _stream.ReadAsyncAll(dataResponse, 0, size); // читаем сообщение
                        read = Encoding.UTF8.GetString(dataResponse, 0, size);

                        textBox3.AppendText("\n" + read); // обновляем UI
                    }*/
                }
            }
            catch
            {
                if (_conn) // если исключение вызвано не отключением пользователя кнопкой
                    do
                    {
                        await Reconnect();
                        await Task.Delay(5000);
                    } while (!_conn);
            }
        }

        private async Task Reconnect()
        {
            textBox1.AppendText(String.Format("\r\nСервер {0}:{1} не отвечает. Попытка переподключения.", _server, _port)); // логируем
            Discon(); // отключаемся 
            await Conn_Disconn(); // подключаемся заново
        }
        private void TextBox2_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)// ентер = нажатие кнопки отправить
            { 
                Create_Message();
            }
        }

        private  void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (_conn) // отключаемся при закрытии формы
            {
                CommonMessage mess = new CommonMessage()// сообщения для отправки
                {
                    Ctrl = 0
                };
                AddInQ(SerializeMessage(mess)); // добавляем в очередь сообщение о отключение
            }
        }
    }
    static class StreamExtension
    {
        public static async Task<int> ReadAsyncAll(this NetworkStream stream, byte[] buffer, int offset, int size)
        {
            int bytes;
            int bytesRead = 0;
            do
            {
                bytes = await stream.ReadAsync(buffer, offset + bytesRead, size - bytesRead);
                if (bytes == 0)
                    throw new IOException();
                bytesRead += bytes;
            } while (bytesRead < size);
            return bytesRead;
        }
    }
}
