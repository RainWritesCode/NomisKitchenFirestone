using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NomisKitchen.Reports;
using UnityEngine.Networking;

namespace NomisKitchen
{
    internal static class WebUpload
    {
        sealed class Job
        {
            public ReportUpload Upload;
            public TaskCompletionSource<ReportAnswer> Done;
            public UnityWebRequest Request;
        }

        static readonly object Gate = new object();
        static readonly Queue<Job> Waiting = new Queue<Job>();
        static Job _active;

        internal static Task<ReportAnswer> Send(ReportUpload upload)
        {
            var job = new Job { Upload = upload, Done = new TaskCompletionSource<ReportAnswer>(TaskCreationOptions.RunContinuationsAsynchronously) };
            lock (Gate) Waiting.Enqueue(job);
            return job.Done.Task;
        }

        internal static void Pump()
        {
            if (_active == null)
            {
                lock (Gate)
                {
                    if (Waiting.Count == 0) return;
                    _active = Waiting.Dequeue();
                }
                Start(_active);
                return;
            }
            if (_active.Request != null && !_active.Request.isDone) return;
            Finish(_active);
            _active = null;
        }

        static void Start(Job job)
        {
            try
            {
                var request = new UnityWebRequest(job.Upload.Url, "POST");
                job.Request = request;
                var body = new UploadHandlerRaw(job.Upload.Body);
                body.contentType = job.Upload.ContentType;
                request.uploadHandler = body;
                request.downloadHandler = new DownloadHandlerBuffer();
                request.timeout = job.Upload.TimeoutSeconds;
                if (!string.IsNullOrEmpty(job.Upload.UserAgent))
                {
                    try { request.SetRequestHeader("User-Agent", job.Upload.UserAgent); }
                    catch { }
                }
                request.SendWebRequest();
            }
            catch (Exception e)
            {
                job.Request?.Dispose();
                job.Request = null;
                job.Done.TrySetResult(new ReportAnswer { Error = e.Message });
            }
        }

        static void Finish(Job job)
        {
            var request = job.Request;
            if (request == null) return;
            try
            {
                job.Done.TrySetResult(new ReportAnswer
                {
                    Status = (int)request.responseCode,
                    Body = request.downloadHandler?.text,
                    Error = request.error,
                });
            }
            catch (Exception e)
            {
                job.Done.TrySetResult(new ReportAnswer { Error = e.Message });
            }
            finally
            {
                request.Dispose();
            }
        }
    }
}
