using EchoBot.Constants;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.Skype.Bots.Media;
using System.Runtime.InteropServices;
using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using OpenAI.Chat;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Blobs;
using System.Text;

namespace EchoBot.Media
{
    /// <summary>
    /// Class SpeechService.
    /// </summary>
    public class SpeechService
    {
        /// <summary>
        /// The is the indicator if the media stream is running
        /// </summary>
        private bool _isRunning = false;
        /// <summary>
        /// The is draining indicator
        /// </summary>
        protected bool _isDraining;

        /// <summary>
        /// The logger
        /// </summary>
        private readonly ILogger _logger;
        private readonly PushAudioInputStream _audioInputStream = AudioInputStream.CreatePushStream(AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1));
        private readonly AudioOutputStream _audioOutputStream = AudioOutputStream.CreatePullStream();

        private readonly SpeechConfig _speechConfig;
        private SpeechRecognizer _recognizer;
        private readonly SpeechSynthesizer _synthesizer;

        /// <summary>
        /// Initializes a new instance of the <see cref="SpeechService" /> class.
        public SpeechService(AppSettings settings, ILogger logger)
        {
            _logger = logger;

            _speechConfig = SpeechConfig.FromSubscription(settings.SpeechConfigKey, settings.SpeechConfigRegion);
            _speechConfig.SpeechSynthesisLanguage = settings.BotLanguage;
            _speechConfig.SpeechRecognitionLanguage = settings.BotLanguage;

            InputValues.Openaiendpoint=settings.OpenaiEndpoint;
            InputValues.Openaikey=settings.OpenaiKey;
            InputValues.SaConnectionString=settings.SaConnectionString;

            var audioConfig = AudioConfig.FromStreamOutput(_audioOutputStream);
            _synthesizer = new SpeechSynthesizer(_speechConfig, audioConfig);

        }

        /// <summary>
        /// Appends the audio buffer.
        /// </summary>
        /// <param name="audioBuffer"></param>
        public async Task AppendAudioBuffer(AudioMediaBuffer audioBuffer)
        {
            if (!_isRunning)
            {
                Start();
                await ProcessSpeech();
            }

            try
            {
                // audio for a 1:1 call
                var bufferLength = audioBuffer.Length;
                if (bufferLength > 0)
                {
                    var buffer = new byte[bufferLength];
                    Marshal.Copy(audioBuffer.Data, buffer, 0, (int)bufferLength);

                    _audioInputStream.Write(buffer);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Exception happend writing to input stream");
            }
        }

        public virtual void OnSendMediaBufferEventArgs(object sender, MediaStreamEventArgs e)
        {
            if (SendMediaBuffer != null)
            {
                SendMediaBuffer(this, e);
            }
        }

        public event EventHandler<MediaStreamEventArgs> SendMediaBuffer;

        /// <summary>
        /// Ends this instance.
        /// </summary>
        /// <returns>Task.</returns>
        public async Task ShutDownAsync()
        {
            if (!_isRunning)
            {
                return;
            }

            if (_isRunning)
            {
                await _recognizer.StopContinuousRecognitionAsync();
                _recognizer.Dispose();
                _audioInputStream.Close();

                _audioInputStream.Dispose();
                _audioOutputStream.Dispose();
                _synthesizer.Dispose();

                _isRunning = false;
            }
        }

        /// <summary>
        /// Starts this instance.
        /// </summary>
        private void Start()
        {
            if (!_isRunning)
            {
                _isRunning = true;
            }
        }
        public static bool ContainsPattern(string text, string pattern)
        {
            int textIndex = 0, patternIndex = 0;

            // Loop through the text
            while (textIndex < text.Length && patternIndex < pattern.Length)
            {
                // If the current character in text matches the current character in pattern
                if (text[textIndex] == pattern[patternIndex])
                {
                    // Move to the next character in pattern
                    patternIndex++;
                }
                // Move to the next character in text regardless
                textIndex++;
            }

            // If we've matched the entire pattern, return true
            return patternIndex == pattern.Length;
        }

        /// <summary>
        /// Processes this instance.
        /// </summary>
        private async Task ProcessSpeech()
        {
            try
            {
                var stopRecognition = new TaskCompletionSource<int>();

                using (var audioInput = AudioConfig.FromStreamInput(_audioInputStream))
                {
                    if (_recognizer == null)
                    {
                        _logger.LogInformation("init recognizer");
                        _recognizer = new SpeechRecognizer(_speechConfig, audioInput);
                    }
                }

                _recognizer.Recognizing += (s, e) =>
                {
                    _logger.LogInformation($"RECOGNIZING: Text={e.Result.Text}");
                };

                _recognizer.Recognized += async (s, e) =>
                {
                    if (e.Result.Reason == ResultReason.RecognizedSpeech)
                    {
                        if (string.IsNullOrEmpty(e.Result.Text))
                            return;

                        _logger.LogInformation($"RECOGNIZED: Text={e.Result.Text}");
                        // We recognized the speech
                        // Now do Speech to Text
                        string audioReceived = e.Result.Text;
                        string keyword = "cosmo";

                        BlobServiceClient client = new BlobServiceClient(InputValues.SaConnectionString);
                        BlobContainerClient container = client.GetBlobContainerClient("localfiles");

                        string fileName = "logs1.txt";


                        AppendBlobClient appendBlobClient = container.GetAppendBlobClient(fileName);

                        byte[] logBytes = Encoding.UTF8.GetBytes(DateTime.Now.ToString() + ": " + audioReceived + "\n\n");
                        using (MemoryStream stream = new MemoryStream(logBytes))
                        {
                            await appendBlobClient.AppendBlockAsync(stream);
                        }


                        BlobClient blob = container.GetBlobClient("model.table");
                        string localFilePath = Path.Combine(Path.GetTempPath(), "model.table");
                        using (FileStream fileStream = File.Open(localFilePath, FileMode.Create, FileAccess.Write))
                        {
                            await blob.DownloadToAsync(fileStream);
                        }
                        var keywordModel = KeywordRecognitionModel.FromFile(localFilePath);

                        using (var audioInput = AudioConfig.FromStreamInput(_audioInputStream))
                        {
                            using var keywordRecognizer = new KeywordRecognizer(audioInput);

                            KeywordRecognitionResult resultKey = await keywordRecognizer.RecognizeOnceAsync(keywordModel);

                            string printKey = "";

                            if (resultKey.Reason == ResultReason.RecognizedKeyword)
                            {
                                printKey+= "Keyword recognized: ";
                            }
                            else
                            {
                                printKey+= "No match found: ";
                            }
                            printKey += resultKey.Text;

                            logBytes = Encoding.UTF8.GetBytes(DateTime.Now.ToString() + ": " + printKey + "\n\n");
                            using (MemoryStream stream = new MemoryStream(logBytes))
                            {
                                await appendBlobClient.AppendBlockAsync(stream);
                            }
                        }

                        //if (ContainsPattern(audioReceived, keyword))
                        //{
                        //    await TextToSpeech(e.Result.Text);
                        //}
                    }
                    else if (e.Result.Reason == ResultReason.NoMatch)
                    {
                        _logger.LogInformation($"NOMATCH: Speech could not be recognized.");
                    }
                };

                _recognizer.Canceled += (s, e) =>
                {
                    _logger.LogInformation($"CANCELED: Reason={e.Reason}");

                    if (e.Reason == CancellationReason.Error)
                    {
                        _logger.LogInformation($"CANCELED: ErrorCode={e.ErrorCode}");
                        _logger.LogInformation($"CANCELED: ErrorDetails={e.ErrorDetails}");
                        _logger.LogInformation($"CANCELED: Did you update the subscription info?");
                    }

                    stopRecognition.TrySetResult(0);
                };

                _recognizer.SessionStarted += async (s, e) =>
                {
                    _logger.LogInformation("\nSession started event.");
                    string greetText = "Hello, My name is " + InputValues.PersonName + " bot. I am here on " + InputValues.PersonName +"'s behalf";
                    await TextToSpeech(greetText);
                };

                _recognizer.SessionStopped += (s, e) =>
                {
                    _logger.LogInformation("\nSession stopped event.");
                    _logger.LogInformation("\nStop recognition.");
                    stopRecognition.TrySetResult(0);
                };

                // Starts continuous recognition. Uses StopContinuousRecognitionAsync() to stop recognition.
                await _recognizer.StartContinuousRecognitionAsync().ConfigureAwait(false);

                // Waits for completion.
                // Use Task.WaitAny to keep the task rooted.
                Task.WaitAny(new[] { stopRecognition.Task });

                // Stops recognition.
                await _recognizer.StopContinuousRecognitionAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException ex)
            {
                _logger.LogError(ex, "The queue processing task object has been disposed.");
            }
            catch (Exception ex)
            {
                // Catch all other exceptions and log
                _logger.LogError(ex, "Caught Exception");
            }

            _isDraining = false;
        }


        private async Task TextToSpeech(string text)
        {
            //text = "My name is bhavesh. I am a bot made by bhavesh. I do as he commands" + text;
            // convert the text to speech
            string update = "cat";
            string blocker = "dog";
            if (ContainsPattern(text, update))
            {
                text = InputValues.Status;
            }
            else if (ContainsPattern(text, blocker))
            {
                text = InputValues.Blocker;
            }
            string inputText = text;
            string errorMessage = "";
            if (!string.IsNullOrEmpty(InputValues.Openaikey) && !string.IsNullOrEmpty(InputValues.Openaiendpoint))
            {
                try
                {
                    AzureKeyCredential credential = new AzureKeyCredential(InputValues.Openaikey);
                    AzureOpenAIClient azureClient = new(new Uri(InputValues.Openaiendpoint), credential);
                    ChatClient chatClient = azureClient.GetChatClient("teamsgptmodel");

                    ChatCompletion completion = chatClient.CompleteChat(
                        new ChatMessage[] {
                            new SystemChatMessage(text),
                        },
                        new ChatCompletionOptions()
                        {
                            Temperature = (float)0.7,
                            MaxTokens = 800,
                            FrequencyPenalty = (float)0,
                            PresencePenalty = (float)0,
                        }
                    );
                    text = completion.Content[0].Text;
                }
                catch (Exception ex)
                {
                    errorMessage = "I am sorry, I cannot reach gpt model";
                }
                //Console.WriteLine($"{completion.Content[0].Text}: {completion.Content[0].Text}");
            }
            else
            {
                errorMessage = "I am sorry, the gpt variables are not set";
            }
            string finalText = text;
            if(!string.IsNullOrEmpty(errorMessage))
            {
                finalText += "  " + errorMessage;
            }
            await WriteLogsToBlob(finalText);

            SpeechSynthesisResult result = await _synthesizer.SpeakTextAsync(finalText);
            // take the stream of the result
            // create 20ms media buffers of the stream
            // and send to the AudioSocket in the BotMediaStream
            using (var stream = AudioDataStream.FromResult(result))
            {
                var currentTick = DateTime.Now.Ticks;
                MediaStreamEventArgs args = new MediaStreamEventArgs
                {
                    AudioMediaBuffers = Util.Utilities.CreateAudioMediaBuffers(stream, currentTick, _logger)
                };
                OnSendMediaBufferEventArgs(this, args);
            }
        }
        /// <summary>
        /// Writes logs to a blob storage.
        /// </summary>
        /// <param name="text">The text to be logged.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task WriteLogsToBlob(string text)
        {
            try
            {
                BlobServiceClient client = new BlobServiceClient(InputValues.SaConnectionString);
                BlobContainerClient container = client.GetBlobContainerClient("localfiles");

                // Write logs to blob
                string fileName = "logs1.txt";
                AppendBlobClient appendBlobClient = container.GetAppendBlobClient(fileName);

                byte[] logBytes = Encoding.UTF8.GetBytes(DateTime.Now.ToString() + ": " + text + "\n\n");
                using (MemoryStream stream = new MemoryStream(logBytes))
                {
                    await appendBlobClient.AppendBlockAsync(stream);
                }

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception occurred while writing logs to blob.");
            }
        }
    }
}
