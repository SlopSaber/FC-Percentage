#nullable enable

using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace FCPercentage.FCPCore
{
    internal static class ScoreMetadataPreparation
    {
        private readonly struct Definition
        {
            private readonly bool exists;
            private readonly int score;

            internal Definition(ScoreModel.NoteScoreDefinition? definition)
            {
                exists = definition != null;
                score = definition?.maxCutScore ?? 0;
            }

            internal int Score => exists ? score : throw new NullReferenceException();
        }

        private readonly struct Element : IComparable<Element>
        {
            private readonly float time;
            internal readonly Definition Definition;

            internal Element(float time, Definition definition)
            {
                this.time = time;
                Definition = definition;
            }

            public int CompareTo(Element other)
            {
                int comparison = time.CompareTo(other.time);
                return comparison != 0 ? comparison : Definition.Score.CompareTo(other.Definition.Score);
            }
        }

        private readonly struct Burst
        {
            internal readonly float Time;
            internal readonly float TailTime;
            internal readonly int SliceCount;
            internal readonly Definition Definition;

            internal Burst(float time, float tailTime, int sliceCount, Definition definition)
            {
                Time = time;
                TailTime = tailTime;
                SliceCount = sliceCount;
                Definition = definition;
            }
        }

        private sealed class Request
        {
            private readonly Element[] notes;
            private readonly Burst[] bursts;
            internal readonly TaskCompletionSource<int> Completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

            internal Request(Element[] notes, Burst[] bursts)
            {
                this.notes = notes;
                this.bursts = bursts;
            }

            internal void Execute()
            {
                try
                {
                    var elements = new List<Element>(1000);
                    elements.AddRange(notes);
                    foreach (Burst burst in bursts)
                    {
                        for (int i = 1; i < burst.SliceCount; i++)
                        {
                            float t = (float)i / (float)(burst.SliceCount - 1);
                            elements.Add(new Element(Interpolate(burst.Time, burst.TailTime, t), burst.Definition));
                        }
                    }
                    elements.Sort();
                    int total = 0;
                    int multiplier = 1;
                    int progress = 0;
                    int threshold = 2;
                    foreach (Element element in elements)
                    {
                        if (multiplier < 8)
                        {
                            if (progress < threshold)
                                progress++;
                            if (progress >= threshold)
                            {
                                multiplier *= 2;
                                progress = 0;
                                threshold = multiplier * 2;
                            }
                        }
                        total = unchecked(total + element.Definition.Score * multiplier);
                    }
                    Completion.TrySetResult(total);
                }
                catch (Exception exception)
                {
                    Completion.TrySetException(exception);
                }
            }
        }

        private static readonly object gate = new object();
        private static readonly Queue<Request> requests = new Queue<Request>();
        private static Task? worker;

        internal static int Compute(IReadonlyBeatmapData beatmapData)
        {
            if (!CanPrepare(beatmapData))
                return ScoreModel.ComputeMaxMultipliedScoreForBeatmap(beatmapData);

            IEnumerable<NoteData> noteItems = beatmapData.GetBeatmapDataItems<NoteData>(0);
            IEnumerable<SliderData> sliderItems = beatmapData.GetBeatmapDataItems<SliderData>(0);
            var notes = new List<Element>();
            foreach (NoteData note in noteItems)
            {
                if (note.scoringType != NoteData.ScoringType.Ignore && note.scoringType != NoteData.ScoringType.NoScore)
                {
                    NoteData.ScoringType scoringType = note.scoringType;
                    float time = note.time;
                    notes.Add(new Element(time, new Definition(ScoreModel.GetNoteScoreDefinition(scoringType))));
                }
            }
            var bursts = new List<Burst>();
            foreach (SliderData slider in sliderItems)
            {
                if (slider.sliderType == SliderData.Type.Burst)
                {
                    int count = slider.sliceCount;
                    if (count > 1)
                    {
                        float time = slider.time;
                        float tailTime = slider.tailTime;
                        var definition = new Definition(ScoreModel.GetNoteScoreDefinition(NoteData.ScoringType.ChainLink));
                        bursts.Add(new Burst(time, tailTime, count, definition));
                    }
                }
            }

            var request = new Request(notes.ToArray(), bursts.ToArray());
            lock (gate)
            {
                requests.Enqueue(request);
                if (worker == null)
                    StartWorker();
            }
            Task<int> task = request.Completion.Task;
            if (!task.IsCompleted)
                ((IAsyncResult)task).AsyncWaitHandle.WaitOne();
            return task.GetAwaiter().GetResult();
        }

        private static float Interpolate(float a, float b, float t)
        {
            return a + (b - a) * t;
        }

        private static bool CanPrepare(IReadonlyBeatmapData beatmapData)
        {
            try
            {
                if (beatmapData == null || beatmapData.GetType() != typeof(BeatmapData))
                    return false;
                Type? elementType = typeof(ScoreModel).GetNestedType("MaxScoreCounterElement", BindingFlags.NonPublic);
                MethodInfo? beatmapItems = typeof(BeatmapData).GetMethod("GetBeatmapDataItems");
                MethodInfo? collectionItems = typeof(BeatmapDataSortedListForTypeAndIds<BeatmapDataItem>).GetMethod("GetItems");
                if (elementType == null || beatmapItems == null || collectionItems == null)
                    return false;

                MethodBase?[] methods =
                {
                    typeof(ScoreModel).GetMethod("ComputeMaxMultipliedScoreForBeatmap"),
                    typeof(ScoreModel).GetMethod("GetNoteScoreDefinition"),
                    elementType.GetConstructor(new[] { typeof(NoteData.ScoringType), typeof(float) }),
                    elementType.GetMethod("CompareTo"),
                    typeof(ScoreModel.NoteScoreDefinition).GetProperty("maxCutScore")?.GetMethod,
                    typeof(ScoreModel.NoteScoreDefinition).GetProperty("executionOrder")?.GetMethod,
                    typeof(ScoreMultiplierCounter).GetConstructor(Type.EmptyTypes),
                    typeof(ScoreMultiplierCounter).GetMethod("Reset"),
                    typeof(ScoreMultiplierCounter).GetMethod("ProcessMultiplierEvent"),
                    typeof(ScoreMultiplierCounter).GetProperty("multiplier")?.GetMethod,
                    typeof(Mathf).GetMethod("LerpUnclamped"),
                    typeof(BeatmapDataItem).GetProperty("time")?.GetMethod,
                    typeof(NoteData).GetProperty("scoringType")?.GetMethod,
                    typeof(SliderData).GetProperty("sliderType")?.GetMethod,
                    typeof(SliderData).GetProperty("sliceCount")?.GetMethod,
                    typeof(SliderData).GetProperty("tailTime")?.GetMethod,
                    beatmapItems,
                    beatmapItems.MakeGenericMethod(typeof(NoteData)),
                    beatmapItems.MakeGenericMethod(typeof(SliderData)),
                    collectionItems,
                    collectionItems.MakeGenericMethod(typeof(NoteData)),
                    collectionItems.MakeGenericMethod(typeof(SliderData)),
                    typeof(global::SortedList<BeatmapDataItem, BeatmapDataItem>).GetProperty("items")?.GetMethod
                };
                foreach (MethodBase? method in methods)
                    if (method == null || Harmony.GetPatchInfo(method) != null)
                        return false;
                foreach (Type type in new[] { typeof(ScoreModel), typeof(ScoreModel.NoteScoreDefinition), typeof(ScoreMultiplierCounter) })
                    if (type.TypeInitializer != null && Harmony.GetPatchInfo(type.TypeInitializer) != null)
                        return false;

                const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
                object? collection = typeof(BeatmapData).GetField("_beatmapDataItemsPerTypeAndId", fields)?.GetValue(beatmapData);
                if (collection?.GetType() != typeof(BeatmapDataSortedListForTypeAndIds<BeatmapDataItem>))
                    return false;
                FieldInfo? listsField = typeof(BeatmapDataSortedListForTypeAndIds<BeatmapDataItem>).GetField("_items", fields);
                var lists = listsField?.GetValue(collection) as IDictionary;
                FieldInfo? listItems = typeof(global::SortedList<BeatmapDataItem, BeatmapDataItem>).GetField("_items", fields);
                if (listsField == null || lists == null || lists.GetType() != listsField.FieldType || listItems == null)
                    return false;
                Type keyType = listsField.FieldType.GetGenericArguments()[0];
                object? defaultComparer = typeof(EqualityComparer<>).MakeGenericType(keyType).GetProperty("Default")?.GetValue(null);
                if (defaultComparer == null || !ReferenceEquals(listsField.FieldType.GetProperty("Comparer")?.GetValue(lists), defaultComparer))
                    return false;
                foreach (object? list in lists.Values)
                    if (list?.GetType() != typeof(global::SortedList<BeatmapDataItem>) || listItems.GetValue(list)?.GetType() != typeof(LinkedList<BeatmapDataItem>))
                        return false;
                var definitions = typeof(ScoreModel).GetField("_scoreDefinitions", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
                    as Dictionary<NoteData.ScoringType, ScoreModel.NoteScoreDefinition>;
                return definitions != null && definitions.GetType() == typeof(Dictionary<NoteData.ScoringType, ScoreModel.NoteScoreDefinition>)
                    && ReferenceEquals(definitions.Comparer, EqualityComparer<NoteData.ScoringType>.Default);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void StartWorker()
        {
            if (ExecutionContext.IsFlowSuppressed())
                ScheduleWorker();
            else
                using (ExecutionContext.SuppressFlow())
                    ScheduleWorker();
        }

        private static void ScheduleWorker()
        {
            worker = Task.Factory.StartNew(Drain, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
            worker.ContinueWith(WorkerCompleted, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private static void Drain()
        {
            while (true)
            {
                Request request;
                lock (gate)
                {
                    if (requests.Count == 0)
                        return;
                    request = requests.Dequeue();
                }
                request.Execute();
            }
        }

        private static void WorkerCompleted(Task completed)
        {
            lock (gate)
            {
                if (ReferenceEquals(worker, completed))
                {
                    worker = null;
                    if (requests.Count != 0)
                        StartWorker();
                }
            }
        }
    }
}
