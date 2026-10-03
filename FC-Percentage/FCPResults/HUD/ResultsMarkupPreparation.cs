#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace FCPercentage.FCPResults.HUD
{
	internal static class ResultsMarkupPreparation
	{
		private static readonly Dictionary<string, Task<PreparedMarkup>> resources = new Dictionary<string, Task<PreparedMarkup>>();
		private static readonly Queue<Request> pending = new Queue<Request>();
		private static readonly object gate = new object();
		private static Task? worker;

		internal static void Prewarm()
		{
			GetPreparation("FCPercentage.FCPResults.HUD.BSML.ResultsScoreResult.bsml");
			GetPreparation("FCPercentage.FCPResults.HUD.BSML.ResultsPercentageResult.bsml");
			GetPreparation("FCPercentage.FCPResults.HUD.BSML.MissionResultsScoreResult.bsml");
			GetPreparation("FCPercentage.FCPResults.HUD.BSML.MissionResultsPercentageResult.bsml");
		}

		internal static bool IsReady(string resource) => GetPreparation(resource).IsCompleted;

		internal static bool TryTake(string resource, out XDocument? document)
		{
			Task<PreparedMarkup> task = GetPreparation(resource);
			document = null;
			if (!task.IsCompleted)
				return false;

			PreparedMarkup prepared = task.GetAwaiter().GetResult();
			if (prepared.Document == null)
				return false;

			// Native macros can mutate XML, so every parse receives an exclusive document.
			resources[resource] = QueuePreparation(resource, prepared.Content);
			document = prepared.Document;
			return true;
		}

		private static Task<PreparedMarkup> GetPreparation(string resource)
		{
			if (!resources.TryGetValue(resource, out Task<PreparedMarkup>? task))
			{
				task = QueuePreparation(resource, null);
				resources.Add(resource, task);
			}
			return task;
		}

		private static Task<PreparedMarkup> QueuePreparation(string resource, string? content)
		{
			if (CultureInfo.CurrentCulture.GetType() != typeof(CultureInfo) || CultureInfo.CurrentUICulture.GetType() != typeof(CultureInfo))
				return Task.FromResult(PreparedMarkup.Failed);

			Request request = new Request(resource, content,
				CultureInfo.ReadOnly((CultureInfo)CultureInfo.CurrentCulture.Clone()),
				CultureInfo.ReadOnly((CultureInfo)CultureInfo.CurrentUICulture.Clone()));
			lock (gate)
			{
				pending.Enqueue(request);
				if (worker == null)
					StartWorker();
			}
			return request.Completion.Task;
		}

		private static void StartWorker()
		{
			if (ExecutionContext.IsFlowSuppressed())
				ScheduleWorker();
			else
			{
				using (ExecutionContext.SuppressFlow())
					ScheduleWorker();
			}
		}

		private static void ScheduleWorker()
		{
			worker = Task.Run(ProcessQueue);
			worker.ContinueWith(WorkerCompleted, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
		}

		private static void WorkerCompleted(Task completed)
		{
			lock (gate)
			{
				if (!ReferenceEquals(worker, completed))
					return;
				worker = null;
				if (pending.Count != 0)
					StartWorker();
			}
		}

		private static void ProcessQueue()
		{
			while (true)
			{
				Request request;
				lock (gate)
				{
					if (pending.Count == 0)
						return;
					request = pending.Dequeue();
				}
				request.Completion.TrySetResult(Prepare(request));
			}
		}

		private static PreparedMarkup Prepare(Request request)
		{
			CultureInfo previousCulture = CultureInfo.CurrentCulture;
			CultureInfo previousUICulture = CultureInfo.CurrentUICulture;
			try
			{
				CultureInfo.CurrentCulture = request.Culture;
				CultureInfo.CurrentUICulture = request.UICulture;
				string? content = request.Content;
				if (content == null)
				{
					using Stream? stream = request.Assembly.GetManifestResourceStream(request.Resource);
					if (stream == null)
						return PreparedMarkup.Failed;
					using StreamReader reader = new StreamReader(stream);
					content = reader.ReadToEnd();
				}
				return new PreparedMarkup(content, XDocument.Parse(content, LoadOptions.SetLineInfo));
			}
			catch (Exception)
			{
				// Speculative failures retain the original caller's resource and parser errors.
				return PreparedMarkup.Failed;
			}
			finally
			{
				CultureInfo.CurrentCulture = previousCulture;
				CultureInfo.CurrentUICulture = previousUICulture;
			}
		}

		private sealed class Request
		{
			internal readonly Assembly Assembly = System.Reflection.Assembly.GetExecutingAssembly();
			internal readonly string Resource;
			internal readonly string? Content;
			internal readonly CultureInfo Culture;
			internal readonly CultureInfo UICulture;
			internal readonly TaskCompletionSource<PreparedMarkup> Completion = new TaskCompletionSource<PreparedMarkup>(TaskCreationOptions.RunContinuationsAsynchronously);

			internal Request(string resource, string? content, CultureInfo culture, CultureInfo uiCulture)
			{
				Resource = resource;
				Content = content;
				Culture = culture;
				UICulture = uiCulture;
			}
		}

		private sealed class PreparedMarkup
		{
			internal static readonly PreparedMarkup Failed = new PreparedMarkup(null, null);
			internal readonly string? Content;
			internal readonly XDocument? Document;

			internal PreparedMarkup(string? content, XDocument? document)
			{
				Content = content;
				Document = document;
			}
		}
	}
}
