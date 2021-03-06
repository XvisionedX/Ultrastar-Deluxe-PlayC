using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using UnityEngine;

// Handles loading and caching of SongMeta and related data structures (e.g. the voices are cached).
public class SongMetaManager : MonoBehaviour
{
    private static readonly object scanLock = new object();

    public int SongsFound { get; private set; }
    public int SongsSuccess { get; private set; }
    public int SongsFailed { get; private set; }

    public static SongMetaManager Instance
    {
        get
        {
            return GameObjectUtils.FindComponentWithTag<SongMetaManager>("SongMetaManager");
        }
    }

    // The collection of songs is static to be persisted across scenes.
    // The collection is filled with song datas from a background thread, thus a thread-safe collection is used.
    private static readonly ConcurrentBag<SongMeta> songMetas = new ConcurrentBag<SongMeta>();

    private static bool isSongScanStarted;
    private static bool isSongScanFinished;
    public static bool IsSongScanFinished
    {
        get
        {
            return isSongScanFinished;
        }
    }

    public void Add(SongMeta songMeta)
    {
        if (songMeta == null)
        {
            throw new ArgumentNullException("songMeta");
        }

        songMetas.Add(songMeta);
    }

    public SongMeta FindSongMeta(Func<SongMeta, bool> predicate)
    {
        SongMeta songMeta = songMetas.Where(predicate).FirstOrDefault();
        return songMeta;
    }

    public SongMeta GetFirstSongMeta()
    {
        return GetSongMetas().FirstOrDefault();
    }

    public IReadOnlyCollection<SongMeta> GetSongMetas()
    {
        return songMetas;
    }

    public void ScanFilesIfNotDoneYet()
    {
        // First check. If the songs have been scanned already,
        // then this will quickly return and allows multiple threads access.
        if (!isSongScanStarted)
        {
            // The songs have not been scanned. Only one thread must perform the scan action.
            lock (scanLock)
            {
                // From here on, reading and writing the isInitialized flag can be considered atomic.
                // Second check. If multiple threads attempted to scan for songs (they passed the first check),
                // then only the first of these threads will start the scan.
                if (!isSongScanStarted)
                {
                    isSongScanStarted = true;
                    isSongScanFinished = false;
                    ScanFilesAsynchronously();
                }
            }
        }
    }

    private void ScanFilesAsynchronously()
    {
        Debug.Log("ScanFilesAsynchronously");

        List<string> txtFiles;
        lock (scanLock)
        {
            SongsFound = 0;
            SongsSuccess = 0;
            SongsFailed = 0;

            FolderScanner scannerTxt = new FolderScanner("*.txt");

            // Find all txt files in the song directories
            txtFiles = ScanForTxtFiles(scannerTxt);
        }

        // Load the txt files in a background thread
        ThreadPool.QueueUserWorkItem(poolHandle =>
        {
            System.Diagnostics.Stopwatch stopwatch = new System.Diagnostics.Stopwatch();
            stopwatch.Start();

            Debug.Log("Started song-scan-thread.");
            lock (scanLock)
            {
                LoadTxtFiles(txtFiles);
                isSongScanFinished = true;
            }
            stopwatch.Stop();
            Debug.Log($"Finished song-scan-thread after {stopwatch.ElapsedMilliseconds} ms.");
        });
    }

    private void LoadTxtFiles(List<string> txtFiles)
    {
        txtFiles.ForEach(delegate (string path)
        {
            try
            {
                Add(SongMetaBuilder.ParseFile(path));
                SongsSuccess++;
            }
            catch (SongMetaBuilderException e)
            {
                Debug.LogWarning(path + "\n" + e.Message);
                SongsFailed++;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                Debug.LogError(path);
                SongsFailed++;
            }
        });
    }

    private List<string> ScanForTxtFiles(FolderScanner scannerTxt)
    {
        List<string> txtFiles = new List<string>();
        List<string> songDirs = SettingsManager.Instance.Settings.GameSettings.songDirs;
        foreach (string songDir in songDirs)
        {
            try
            {
                List<string> txtFilesInSongDir = scannerTxt.GetFiles(songDir);
                txtFiles.AddRange(txtFilesInSongDir);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }
        SongsFound = txtFiles.Count;
        Debug.Log($"Found {SongsFound} songs in {songDirs.Count} configured song directories");
        return txtFiles;
    }

    public int GetNumberOfSongsFound()
    {
        return SongsFound;
    }

    public void WaitUntilSongScanFinished()
    {
        ScanFilesIfNotDoneYet();
        float startTimeInSeconds = Time.time;
        float timeoutInSeconds = 2;
        while ((startTimeInSeconds + timeoutInSeconds) > Time.time)
        {
            if (isSongScanFinished)
            {
                return;
            }
            Thread.Sleep(100);
        }
        Debug.LogError("Song scan did not finish - timeout reached.");
    }
}
