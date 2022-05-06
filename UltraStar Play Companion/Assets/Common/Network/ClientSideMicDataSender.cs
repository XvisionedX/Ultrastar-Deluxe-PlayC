﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UniInject;
using UnityEngine;
using UniRx;
using CircularBuffer;

// Disable warning about fields that are never assigned, their values are injected.
#pragma warning disable CS0649

public class ClientSideMicDataSender : MonoBehaviour, INeedInjection
{
    public static ClientSideMicDataSender Instance
    {
        get
        {
            return GameObjectUtils.FindComponentWithTag<ClientSideMicDataSender>("ClientSideMicrophoneDataSender");
        }
    }

    [Inject]
    private MicSampleRecorder micSampleRecorder;

    [Inject]
    private Settings settings;

    [Inject]
    private ClientSideConnectRequestManager clientSideConnectRequestManager;
    
    private TcpClient tcpClient;
    private NetworkStream tcpClientStream;
    private StreamReader tcpClientStreamReader;
    private StreamWriter tcpClientStreamWriter;
    private IPEndPoint serverSideTcpClientEndPoint;

    public bool IsConnected => serverSideTcpClientEndPoint != null
                                && tcpClient != null
                                && tcpClientStream != null
                                && tcpClientStreamReader != null
                                && tcpClientStreamWriter != null;

    private IAudioSamplesAnalyzer audioSamplesAnalyzer;

    private Thread receiveDataThread;
    private Thread serverStillAliveCheckThread;

    private SongMeta songMeta;
    private readonly CircularBuffer<PositionInSongData> receivedPositionInSongTimes = new(3);
    private PositionInSongData bestPositionInSongData;
    private int lastAnalyzedBeat;

    private bool HasPositionInSong => songMeta != null && bestPositionInSongData != null;

    private bool receivedStopRecordingMessage;
    private bool receivedStartRecordingMessage;

    private void Start()
    {
        ResetPositionInSong();

        UpdateAudioSamplesAnalyzer();
        micSampleRecorder.FinalSampleRate.Subscribe(_ => UpdateAudioSamplesAnalyzer());

        clientSideConnectRequestManager.ConnectEventStream.Subscribe(UpdateConnectionStatus);
        micSampleRecorder.RecordingEventStream.Subscribe(HandleNewMicSamples);
        micSampleRecorder.IsRecording.Subscribe(HandleRecordingStatusChanged);

        // Receive messages from server (i.e. from main game)
        receiveDataThread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    if (IsConnected)
                    {
                        while (tcpClientStream.DataAvailable)
                        {
                            ReadMessageFromServer();
                        }
                    }
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    CloseNetworkConnection();
                }

                Thread.Sleep(250);
            }
        });
        receiveDataThread.Start();

        serverStillAliveCheckThread = new Thread(() =>
        {
            while (true)
            {
                if (IsConnected)
                {
                    CheckServerStillAlive();
                }
                Thread.Sleep(1500);
            }
        });
        serverStillAliveCheckThread.Start();
    }

    private void CheckServerStillAlive()
    {
        try
        {
            // If there is new data available, then the client is still alive.
            if (!tcpClientStream.DataAvailable)
            {
                // Try to send something to the client.
                // If this fails with an Exception, then the connection has been lost and the client has to reconnect.
                SendMessageToServer(new StillAliveCheckDto());
            }
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            Debug.LogError("Failed sending data to server. Closing connection.");
            CloseNetworkConnection();
        }
    }

    private void Update()
    {
        if (receivedStopRecordingMessage)
        {
            receivedStopRecordingMessage = false;

            ResetPositionInSong();

            // Must be called from main thread.
            Debug.Log("Stopping recording because of message from server");
            micSampleRecorder.StopRecording();
        }
        if (receivedStartRecordingMessage)
        {
            receivedStartRecordingMessage = false;

            // Must be called from main thread.
            Debug.Log("Starting recording because of message from server");
            micSampleRecorder.StartRecording();
        }

        if (HasPositionInSong
            && bestPositionInSongData.UnixTimeInMillisWhenReceivedPositionInSong + 30000 < TimeUtils.GetUnixTimeMilliseconds())
        {
            // Did not receive new position in song for some time. Probably not in sing scene anymore.
            ResetPositionInSong();
        }

        if (clientSideConnectRequestManager.IsConnected
            && !IsConnected)
        {
            // The connection for messaging was closed. Try reconnect.
            clientSideConnectRequestManager.CloseConnectionAndReconnect();
        }
    }

    private void UpdateAudioSamplesAnalyzer()
    {
        audioSamplesAnalyzer = AbstractMicPitchTracker.CreateAudioSamplesAnalyzer(EPitchDetectionAlgorithm.Dywa, micSampleRecorder.FinalSampleRate.Value);
        audioSamplesAnalyzer.Enable();
    }

    private void HandleRecordingStatusChanged(bool isRecording)
    {
        if (isRecording && HasPositionInSong)
        {
            // Analyze the following beats, not past beats.
            lastAnalyzedBeat = (int)BpmUtils.MillisecondInSongToBeat(songMeta, GetEstimatedPositionInSongInMillis());
            Debug.Log($"HandleRecordingStatusChanged - lastAnalyzedBeat: {lastAnalyzedBeat}");
        }
    }

    private void HandleNewMicSamples(RecordingEvent recordingEvent)
    {
        if (!IsConnected)
        {
            return;
        }

        // Do pitch detection
        if (HasPositionInSong)
        {
            AnalyzeMicSamplesCorrespondingToBeatsInSong(recordingEvent);
        }
        else
        {
            AnalyzeNewestMicSamples(recordingEvent);
        }
    }

    private void AnalyzeMicSamplesCorrespondingToBeatsInSong(RecordingEvent recordingEvent)
    {
        // Check if can analyze new beat
        double estimatedPositionInSongInMillis = GetEstimatedPositionInSongInMillis();
        double positionInSongConsideringMicDelay = estimatedPositionInSongInMillis - settings.MicProfile.DelayInMillis;
        int currentBeatConsideringMicDelay = (int)BpmUtils.MillisecondInSongToBeat(songMeta, positionInSongConsideringMicDelay);
        // int currentBeat = (int)BpmUtils.MillisecondInSongToBeat(songMeta, estimatedPositionInSongInMillis);
        // Debug.Log($"currentBeat: {currentBeat}, withDelay: {currentBeatConsideringMicDelay} (diff: {currentBeat - currentBeatConsideringMicDelay})");
        if (currentBeatConsideringMicDelay <= lastAnalyzedBeat)
        {
            return;
        }

        // Do not analyze more than 100 beats (might missed some beats while app was in background)
        int firstNextBeatToAnalyze = Math.Max(lastAnalyzedBeat + 1, currentBeatConsideringMicDelay - 100);
        // Debug.Log($"Analyzing beats from {nextBeatToAnalyze} to {currentBeat} ({currentBeat - lastAnalyzedBeat} beats, at frame {Time.frameCount}, at systime {TimeUtils.GetUnixTimeMilliseconds()})");

        List<BeatPitchEvent> beatPitchEvents = new();
        int loopCount = 0;
        int maxLoopCount = 100;
        for (int beat = firstNextBeatToAnalyze; beat <= currentBeatConsideringMicDelay; beat++)
        {
            PitchEvent pitchEvent = AnalyzeMicSamplesOfBeat(recordingEvent, beat, estimatedPositionInSongInMillis);
            int midiNote = pitchEvent != null
                ? pitchEvent.MidiNote
                : -1;
            beatPitchEvents.Add(new BeatPitchEvent(midiNote, beat));
            // Debug.Log($"Analyzed beat {beat}: midiNote: {midiNote}");

            loopCount++;
            if (loopCount > maxLoopCount)
            {
                // Emergency exit
                Debug.LogWarning($"Took emergency exit out of loop. Analyzed {maxLoopCount} beats and still not finished?");
            }
        }

        // Send all events int one message
        List<BeatPitchEventDto> beatPitchEventDtos = beatPitchEvents
            .Select(it => new BeatPitchEventDto(it.MidiNote, it.Beat))
            .ToList();
        if (beatPitchEventDtos.Count > 3)
        {
            Debug.LogWarning($"Sending {beatPitchEventDtos.Count} beats to server: {beatPitchEventDtos.Select(it => it.Beat).ToCsv(", ")}");
        }
        SendMessageToServer(new BeatPitchEventsDto(beatPitchEventDtos));

        lastAnalyzedBeat = currentBeatConsideringMicDelay;
    }

    private void AnalyzeNewestMicSamples(RecordingEvent recordingEvent)
    {
        PitchEvent pitchEvent = audioSamplesAnalyzer.ProcessAudioSamples(
            recordingEvent.MicSamples,
            recordingEvent.NewSamplesStartIndex,
            recordingEvent.NewSamplesEndIndex,
            GetMicProfileWithFinalSampleRate());

        int midiNote = pitchEvent != null
            ? pitchEvent.MidiNote
            : -1;
        BeatPitchEventDto beatPitchEventDto = new BeatPitchEventDto(midiNote, -1);
        SendMessageToServer(new BeatPitchEventsDto(beatPitchEventDto));
    }

    private double GetEstimatedPositionInSongInMillis()
    {
        if (!HasPositionInSong)
        {
            Debug.LogWarning("GetEstimatedPositionInSongInMillis called without position in song");
            return 0;
        }

        return bestPositionInSongData.EstimatedPositionInSongInMillis;
    }

    private PitchEvent AnalyzeMicSamplesOfBeat(RecordingEvent recordingEvent, int beat, double positionInSongInMillis)
    {
        PitchEvent pitchEvent = AbstractMicPitchTracker.AnalyzeBeat(
            songMeta,
            beat,
            positionInSongInMillis,
            GetMicProfileWithFinalSampleRate(),
            recordingEvent.MicSamples,
            audioSamplesAnalyzer);
        return pitchEvent;
    }

    private MicProfile GetMicProfileWithFinalSampleRate()
    {
        // The MicProfile in the settings may use a SampleRate of 0 for "best available".
        // The pitch detection algorithm needs the proper value.
        MicProfile micProfile = new MicProfile(settings.MicProfile);
        micProfile.SampleRate = micSampleRecorder.FinalSampleRate.Value;
        return micProfile;
    }

    private void SendMessageToServer(JsonSerializable jsonSerializable)
    {
        try
        {
            tcpClientStreamWriter.WriteLine(jsonSerializable.ToJson());
            tcpClientStreamWriter.Flush();
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            Debug.LogError($"Failed to send pitch to server");
            CloseNetworkConnection();
        }
    }

    private void ReadMessageFromServer()
    {
        string receivedLine = tcpClientStreamReader.ReadLine();
        if (receivedLine.IsNullOrEmpty())
        {
            return;
        }

        receivedLine = receivedLine.Trim();
        if (!receivedLine.StartsWith("{")
            || !receivedLine.EndsWith("}"))
        {
            Debug.LogWarning($"Received invalid message from server: {receivedLine}");
            return;
        }

        HandleJsonMessageFromServer(receivedLine);
    }

    private void HandleJsonMessageFromServer(string json)
    {
        CompanionAppMessageDto companionAppMessageDto = null;
        try
        {
            companionAppMessageDto = JsonConverter.FromJson<CompanionAppMessageDto>(json);
        }
        catch (Exception e)
        {
            Debug.Log($"Exception while parsing message from server: {json}");
            Debug.LogException(e);
        }

        switch (companionAppMessageDto.MessageType)
        {
            case CompanionAppMessageType.StillAliveCheck:
                // Nothing to do. If the connection would not be still alive anymore, then this message would have failed already.
                return;
            case CompanionAppMessageType.PositionInSong:
                HandlePositionInSongMessage(JsonConverter.FromJson<PositionInSongDto>(json));
                return;
            case CompanionAppMessageType.MicProfile:
                HandleMicProfileMessage(JsonConverter.FromJson<MicProfileMessageDto>(json));
                return;
            case CompanionAppMessageType.StopRecording:
                // Must be called from main thread
                receivedStopRecordingMessage = true;
                return;
            case CompanionAppMessageType.StartRecording:
                // Must be called from main thread
                receivedStartRecordingMessage = true;
                return;
            default:
                Debug.Log($"Unknown MessageType {companionAppMessageDto.MessageType} in JSON from server: {json}");
                return;
        }
    }

    private void HandleMicProfileMessage(MicProfileMessageDto micProfileMessageDto)
    {
        Debug.Log($"Received new mic profile: {micProfileMessageDto.ToJson()}");

        MicProfile micProfile = new MicProfile(settings.MicProfile.Name);
        micProfile.Amplification = micProfileMessageDto.Amplification;
        micProfile.NoiseSuppression = micProfileMessageDto.NoiseSuppression;
        micProfile.SampleRate = micProfileMessageDto.SampleRate;
        micProfile.DelayInMillis = micProfileMessageDto.DelayInMillis;
        micProfile.Color = Colors.CreateColor(micProfileMessageDto.HexColor);

        settings.MicProfile = micProfile;
    }

    private void HandlePositionInSongMessage(PositionInSongDto positionInSongDto)
    {
        double estimatedPositionInSongInMillis = GetEstimatedPositionInSongInMillis();

        PositionInSongData positionInSongData = new(positionInSongDto.PositionInSongInMillis, TimeUtils.GetUnixTimeMilliseconds());
        receivedPositionInSongTimes.PushBack(positionInSongData);
        songMeta = new SongMeta
        {
            Bpm = positionInSongDto.SongBpm,
            Gap = positionInSongDto.SongGap,
        };

        // If beats have been analyzed prematurely, then redo analysis.
        int currentBeat = (int)BpmUtils.MillisecondInSongToBeat(songMeta, GetEstimatedPositionInSongInMillis());
        if (lastAnalyzedBeat > currentBeat)
        {
            lastAnalyzedBeat = currentBeat;
        }

        // Use the "received position in the song" which has the least discrepancy
        // with respect to the "estimated position in song" of previously received times.
        // This makes the time more resilient against outliers (e.g. when a message was delivered with big delay).
        float GetTimeError(PositionInSongData time)
        {
            double resultError = 0;
            receivedPositionInSongTimes
                .Where(otherTime => otherTime.UnixTimeInMillisWhenReceivedPositionInSong != time.UnixTimeInMillisWhenReceivedPositionInSong)
                .ForEach(otherTime =>
                {
                    double offset = Math.Abs(time.EstimatedPositionInSongInMillis - otherTime.EstimatedPositionInSongInMillis);
                    resultError += offset;
                });
            return (float)resultError;
        }
        bestPositionInSongData = receivedPositionInSongTimes.FindMinElement(time => GetTimeError(time));

        Debug.Log($"Received position in song: {positionInSongDto.ToJson()}, new best position in song {bestPositionInSongData.ToJson()}");
    }

    private void ResetPositionInSong()
    {
        Debug.Log("Resetting position in song");
        songMeta = null;
        bestPositionInSongData = null;
        receivedPositionInSongTimes.Clear();
        lastAnalyzedBeat = -1;
    }

    private void UpdateConnectionStatus(ConnectEvent connectEvent)
    {
        if (connectEvent.IsSuccess
            && connectEvent.MessagingPort > 0
            && connectEvent.ServerIpEndPoint != null)
        {
            serverSideTcpClientEndPoint = new IPEndPoint(connectEvent.ServerIpEndPoint.Address, connectEvent.MessagingPort);

            CloseNetworkConnection();
            try
            {
                tcpClient = new TcpClient();
                tcpClient.NoDelay = true;
                tcpClient.Connect(serverSideTcpClientEndPoint);
                tcpClientStream = tcpClient.GetStream();
                tcpClientStreamReader = new StreamReader(tcpClientStream);
                tcpClientStreamWriter = new StreamWriter(tcpClientStream);
                tcpClientStreamWriter.AutoFlush = true;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                CloseNetworkConnection();
            }
        }
        else
        {
            serverSideTcpClientEndPoint = null;
            micSampleRecorder.StopRecording();

            // Already disconnected.
            // Do not try to call reconnect (i.e. disconnect then connect) because it would cause a stack overflow.
            CloseNetworkConnection();
        }
    }

    private void OnDestroy()
    {
        CloseNetworkConnection();
    }

    private void CloseNetworkConnection()
    {
        tcpClientStream?.Close();
        tcpClientStream = null;
        tcpClient?.Close();
        tcpClient = null;
    }

    private class PositionInSongData : JsonSerializable
    {
        public double ReceivedPositionInSongInMillis { get; private set; }
        public long UnixTimeInMillisWhenReceivedPositionInSong { get; private set; }
        public double EstimatedPositionInSongInMillis => ReceivedPositionInSongInMillis + (TimeUtils.GetUnixTimeMilliseconds() - UnixTimeInMillisWhenReceivedPositionInSong);

        public PositionInSongData(double receivedPositionInSongInMillis, long unixTimeInMillisWhenReceivedPositionInSong)
        {
            ReceivedPositionInSongInMillis = receivedPositionInSongInMillis;
            UnixTimeInMillisWhenReceivedPositionInSong = unixTimeInMillisWhenReceivedPositionInSong;
        }
    }
}
