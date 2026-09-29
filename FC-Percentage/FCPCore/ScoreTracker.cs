#nullable enable

using System;
using Zenject;
using FCPercentage.FCPCore.Configuration;
using System.Collections.Generic;
using static NoteData;
using SiraUtil.Logging;

namespace FCPercentage.FCPCore
{
	public class ScoreTracker : IInitializable, IDisposable, ICutScoreBufferDidFinishReceiver
	{
		private readonly SiraLog logger;
		[InjectOptional] private GameplayCoreSceneSetupData sceneSetupData = null!;
		[Inject] private PlayerDataModel playerDataModel = null!;
		private readonly ScoreController scoreController;
		private readonly ScoreManager scoreManager;
		private readonly ComboController comboController;

		private readonly Dictionary<CutScoreBuffer, PendingScoreEvent> CutScoreBufferPendingScoreEvent;
		private readonly List<PendingScoreEvent> pendingScoreEvents;
		private int pendingScoreEventIndex;

		private PlayerLevelStatsData GetPlayerLevelStatsData(PlayerDataModel playerDataModel, BeatmapKey beatmap) => playerDataModel.playerData.TryGetPlayerLevelStatsData(beatmap);

		public ScoreTracker(SiraLog logger, [InjectOptional] ScoreController scoreController, [InjectOptional] ComboController comboController, ScoreManager scoreManager)
		{
			this.logger = logger;
			this.scoreManager = scoreManager;
			this.scoreController = scoreController;
			this.comboController = comboController;

			CutScoreBufferPendingScoreEvent = new Dictionary<CutScoreBuffer, PendingScoreEvent>();
			pendingScoreEvents = new List<PendingScoreEvent>();
			pendingScoreEventIndex = 0;
		}

		private void ComboController_comboBreakingEventHappenedEvent() => scoreManager.BreakCombo();

		public void Initialize()
		{
			// Do not initialize if one of those are null
			if (playerDataModel == null || sceneSetupData == null || scoreManager == null)
				return;

			// Reset ScoreManager at level start
			PlayerLevelStatsData stats = GetPlayerLevelStatsData(playerDataModel, sceneSetupData.beatmapKey);
			if (stats == null || sceneSetupData.transformedBeatmapData == null)
				return;
			scoreManager.ResetScoreManager(stats, sceneSetupData.transformedBeatmapData, sceneSetupData.colorScheme);

			// Assign events
			if (scoreController != null)
			{
				scoreController.scoringForNoteStartedEvent += ScoreController_scoringForNoteStartedEvent;
			}
			if (comboController != null)
				comboController.comboBreakingEventHappenedEvent += ComboController_comboBreakingEventHappenedEvent;
		}

		public void Dispose()
		{
			// Unassign events
			if (scoreController != null)
			{
				scoreController.scoringForNoteStartedEvent -= ScoreController_scoringForNoteStartedEvent;
			}
			if (comboController != null)
				comboController.comboBreakingEventHappenedEvent -= ComboController_comboBreakingEventHappenedEvent;
		}

		private void ScoreController_scoringForNoteStartedEvent(ScoringElement scoringElement)
		{
			// Ignore bombs
			if (IsBomb(scoringElement))
				return;

			int multiplier = GetFcMultiplier(scoringElement);
			//logger.Notice($"noteCount[{noteCount}]");
			if (scoringElement is GoodCutScoringElement goodCutScoringElement)
			{
				CutScoreBuffer cutScoreBuffer = (CutScoreBuffer)goodCutScoringElement.cutScoreBuffer;
				PendingScoreEvent pendingScoreEvent = new PendingScoreEvent(scoringElement.time, pendingScoreEventIndex++, scoringElement.noteData.colorType, goodCutScoringElement.maxPossibleCutScore, multiplier);
				pendingScoreEvents.Add(pendingScoreEvent);

				if (goodCutScoringElement.isFinished)
					pendingScoreEvent.Score = cutScoreBuffer.cutScore;
				else
				{
					CutScoreBufferPendingScoreEvent[cutScoreBuffer] = pendingScoreEvent;
					goodCutScoringElement.cutScoreBuffer.RegisterDidFinishReceiver(this);
				}
			}
			else if (IsScoreableColorNote(scoringElement.noteData))
			{
				pendingScoreEvents.Add(new PendingScoreEvent(scoringElement.time, pendingScoreEventIndex++, scoringElement.noteData.colorType, scoringElement.maxPossibleCutScore, multiplier, true));
			}

			ProcessPendingScoreEvents();
		}

		public void HandleCutScoreBufferDidFinish(CutScoreBuffer cutScoreBuffer)
		{
			if (CutScoreBufferPendingScoreEvent.TryGetValue(cutScoreBuffer, out PendingScoreEvent pendingScoreEvent))
			{
				pendingScoreEvent.Score = cutScoreBuffer.cutScore;
				CutScoreBufferPendingScoreEvent.Remove(cutScoreBuffer);
				ProcessPendingScoreEvents();
			}
			else
				logger.Error("HandleCutScoreBufferDidFinish: Unable to get pending score event from CutScoreBufferPendingScoreEvent!");

			cutScoreBuffer.UnregisterDidFinishReceiver(this);
		}

		private void ProcessPendingScoreEvents()
		{
			pendingScoreEvents.Sort((x, y) =>
			{
				int timeCompare = x.Time.CompareTo(y.Time);
				return timeCompare != 0 ? timeCompare : x.Index.CompareTo(y.Index);
			});

			while (pendingScoreEvents.Count > 0 && pendingScoreEvents[0].CanProcess)
			{
				PendingScoreEvent pendingScoreEvent = pendingScoreEvents[0];
				pendingScoreEvents.RemoveAt(0);

				if (pendingScoreEvent.EstimateAtCurrentPercentage)
					scoreManager.AddEstimatedScoreAtCurrentPercentage(pendingScoreEvent.ColorType, pendingScoreEvent.MaxScore, pendingScoreEvent.Multiplier);
				else if (pendingScoreEvent.Score.HasValue)
					scoreManager.AddScore(pendingScoreEvent.ColorType, pendingScoreEvent.Score.Value, pendingScoreEvent.MaxScore, pendingScoreEvent.Multiplier);
			}
		}

		private int GetFcMultiplier(ScoringElement scoringElement) => PluginConfig.Instance.IgnoreMultiplier ? 8 : scoringElement.maxMultiplier;

		private bool IsBomb(ScoringElement scoringElement) => IsBomb(scoringElement.noteData);
		private bool IsBomb(NoteData noteData) => noteData.gameplayType == GameplayType.Bomb;
		private bool IsScoreableColorNote(NoteData noteData) => noteData.colorType == ColorType.ColorA || noteData.colorType == ColorType.ColorB;

		private class PendingScoreEvent
		{
			internal readonly float Time;
			internal readonly int Index;
			internal readonly ColorType ColorType;
			internal readonly int MaxScore;
			internal readonly int Multiplier;
			internal readonly bool EstimateAtCurrentPercentage;
			internal int? Score;

			internal bool CanProcess => EstimateAtCurrentPercentage || Score.HasValue;

			internal PendingScoreEvent(float time, int index, ColorType colorType, int maxScore, int multiplier, bool estimateAtCurrentPercentage = false)
			{
				Time = time;
				Index = index;
				ColorType = colorType;
				MaxScore = maxScore;
				Multiplier = multiplier;
				EstimateAtCurrentPercentage = estimateAtCurrentPercentage;
			}
		}
	}
}
