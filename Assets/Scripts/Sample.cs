using System;
using System.IO;
using System.Net.Http;
using Cysharp.Threading.Tasks;
using Google.Cloud.Speech.V2;
using Google.Apis.Auth.OAuth2;
using Grpc.Auth;
using Grpc.Net.Client;
using UnityEngine;
using Cysharp.Net.Http;
using Google.Protobuf;

public class Sample : MonoBehaviour
{
    private struct ServiceAccountKey
    {
        public string type;
        public string project_id;
        public string private_key_id;
        public string private_key;
        public string client_email;
        public string client_id;
        public string auth_uri;
        public string token_uri;
        public string auth_provider_x509_cert_url;
        public string client_x509_cert_url;
        public string universe_domain;
    }

    private string _keyText;
    private string _projectId;

    private AudioClip _audioClip;
    private Channel<byte[]> _channel = null;
    private int _lastPosition = 0;

    [SerializeField]
    private AudioSource _source;

    const int SampleRate = 16000;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        _keyText = File.ReadAllText("google_service_account.json");
        var key = JsonUtility.FromJson<ServiceAccountKey>(_keyText);
        _projectId = key.project_id;
        UnityEngine.Debug.Log(_keyText);
        foreach (var device in Microphone.devices)
        {
            Debug.Log($"Device: {device}");
        }
    }

    // Update is called once per frame
    void Update()
    {
        if (Microphone.IsRecording(Microphone.devices[0]))
        {
            var currentPosition = Microphone.GetPosition(Microphone.devices[0]);
            if (currentPosition != _lastPosition)
            {
                if (currentPosition < _lastPosition)
                {
                    UnityEngine.Debug.Log($"currentPosition {currentPosition} lastPos {_lastPosition} sample {_audioClip.samples} size {_audioClip.samples - _lastPosition}");
                    var samplesToEnd = _audioClip.samples - _lastPosition;
                    var audioDataToEnd = new float[samplesToEnd];
                    _audioClip.GetData(audioDataToEnd, _lastPosition);
                    var byteDataToEnd = ConvertAudioClipDataToBytes(audioDataToEnd);
                    _channel.Writer.TryWrite(byteDataToEnd);

                    // バッファの先頭からのデータを取得
                    if (currentPosition > 0)
                    {
                        var audioDataFromStart = new float[currentPosition];
                        _audioClip.GetData(audioDataFromStart, 0);
                        var byteDataFromStart = ConvertAudioClipDataToBytes(audioDataFromStart);
                        _channel.Writer.TryWrite(byteDataFromStart);
                    }
                }
                else
                {
                    var samplesToRead = currentPosition - _lastPosition;
                    var audioData = new float[samplesToRead];
                    _audioClip.GetData(audioData, _lastPosition);
                    var byteData = ConvertAudioClipDataToBytes(audioData);
                    _channel.Writer.TryWrite(byteData);
                }
            }
            _lastPosition = currentPosition;
        }
    }

    public void OnStart()
    {
        _channel = Channel.CreateSingleConsumerUnbounded<byte[]>(); ;
        _audioClip = Microphone.Start(Microphone.devices[0], true, 5, 16000);
        _lastPosition = 0;
        StartStt(_channel.Reader);
    }

    public void OnStop()
    {
        _channel.Writer.TryComplete();
        _channel = null;

        Microphone.End(Microphone.devices[0]);

        _source.clip = _audioClip;
        _source.Play();
        _audioClip = null;
    }

    private byte[] ConvertAudioClipDataToBytes(float[] audioData)
    {
        // float配列を16bit PCM形式のbyte配列に変換
        byte[] byteData = new byte[audioData.Length * 2]; // 16bit = 2バイト
        for (int i = 0; i < audioData.Length; i++)
        {
            short sample = (short)(audioData[i] * short.MaxValue);
            byteData[i * 2] = (byte)(sample & 0xFF);       // 下位バイト
            byteData[i * 2 + 1] = (byte)((sample >> 8) & 0xFF); // 上位バイト
        }
        return byteData;
    }

    private void StartStt(ChannelReader<byte[]> dataChannel)
    {
        UniTask.RunOnThreadPool(async () =>
        {
            try
            {
                // サービスアカウントキーのパスを設定
                var credential = GoogleCredential.FromJson(_keyText).CreateScoped(SpeechClient.DefaultScopes);

                var handler = new YetAnotherHttpHandler();
                var client = new HttpClient(handler);

                Debug.Log("Start");
                // gRPCチャンネルを作成
                var channel = GrpcChannel.ForAddress($"https://speech.googleapis.com", new GrpcChannelOptions
                {
                    HttpClient = client,
                    Credentials = credential.ToChannelCredentials()
                });

                Debug.Log("Channel Created");
                var setting = new SpeechSettings();
                // Speech-to-Textクライアントの作成
                var builder = new SpeechClientBuilder();
                builder.CallInvoker = channel.CreateCallInvoker();
                var speechClient = builder.Build();

                await RecognizeSpeech(speechClient, dataChannel);
            }
            catch (Exception e)
            {
                Debug.LogError($"Error: {e.Message} {e.StackTrace}");
            }
        }).Forget();
    }

    private async UniTask RecognizeSpeech(SpeechClient speechClient, ChannelReader<byte[]> dataChannel)
    {
        try
        {
            var streamer = speechClient.StreamingRecognize();


            var tcs = SetReceiver(streamer);

            var recognitionConfig = new RecognitionConfig()
            {
                ExplicitDecodingConfig = new ExplicitDecodingConfig()
                {
                    Encoding = ExplicitDecodingConfig.Types.AudioEncoding.Linear16,
                    SampleRateHertz = SampleRate,
                    AudioChannelCount = 1,
                },
                Model = "long",
                LanguageCodes = { "ja-JP" },
            };

            var request = new StreamingRecognizeRequest();
            request.StreamingConfig = new StreamingRecognitionConfig()
            {
                Config = recognitionConfig,
            };
            request.Recognizer = $"projects/{_projectId}/locations/global/recognizers/_";

            await streamer.WriteAsync(request);

            while (await dataChannel.WaitToReadAsync())
            {
                while (dataChannel.TryRead(out var audioData))
                {
                    var audioRequest = new StreamingRecognizeRequest();
                    UnityEngine.Debug.Log(audioData.Length);
                    audioRequest.Audio = ByteString.CopyFrom(audioData.AsSpan());
                    await streamer.WriteAsync(audioRequest);
                }
            }

            UnityEngine.Debug.Log("Complete");
            await streamer.WriteCompleteAsync();

            await tcs.Task;
            streamer.Dispose();

            speechClient = null;
        }
        catch(OperationCanceledException e)
        {
            Debug.Log($"Canceled: {e.Message}");
        }
        catch (Exception e)
        {
            Debug.LogError($"Error: {e.Message}");
        }
    }

    private UniTaskCompletionSource SetReceiver(SpeechClient.StreamingRecognizeStream streamer)
    {
        var tcs = new UniTaskCompletionSource();

        UniTask.RunOnThreadPool(async () => {
            while (await streamer.GetResponseStream().MoveNextAsync())
            {
                var response = streamer.GetResponseStream().Current;
                foreach (var result in response.Results)
                {
                    if(result.IsFinal)
                    {
                        foreach (var alternative in result.Alternatives)
                        {
                            Debug.Log($"Transcript: {alternative.Transcript}");
                        }
                    }
                }
            }
            tcs.TrySetResult();
        }).Forget();

        return tcs;
    }
}
