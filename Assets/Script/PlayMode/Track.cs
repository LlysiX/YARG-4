using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using YARG.Data;
using YARG.Input;
using YARG.Pools;
using YARG.UI;
using YARG.Util;

namespace YARG.PlayMode {
	public class Track : MonoBehaviour {
		public const float TRACK_SPAWN_OFFSET = 3f;

		public delegate void StarpowerMissAction();
		public event StarpowerMissAction StarpowerMissEvent;

		public PlayerManager.Player player;

		private bool strummed = false;
		private FiveFretInputStrategy input;

		[SerializeField]
		private Camera trackCamera;

		[SerializeField]
		private MeshRenderer trackRenderer;
		[SerializeField]
		private Transform hitWindow;

		[Space]
		[SerializeField]
		private Fret[] frets;
		[SerializeField]
		private Color[] fretColors;
		[SerializeField]
		private NotePool notePool;
		[SerializeField]
		private Pool genericPool;

		[Space]
		[SerializeField]
		private TextMeshPro comboText;
		[SerializeField]
		private MeshRenderer comboMeterRenderer;
		[SerializeField]
		private MeshRenderer starpowerBarTop;

		public float RelativeTime => Play.Instance.SongTime + ((TRACK_SPAWN_OFFSET + 1.75f) / player.trackSpeed);

		public EventInfo StarpowerSection {
			get;
			private set;
		} = null;

		private float starpowerCharge;
		private bool starpowerActive;

		private int _combo = 0;
		private int Combo {
			get => _combo;
			set {
				_combo = value;

				// End starpower if combo ends
				if (StarpowerSection?.time <= Play.Instance.SongTime && value == 0) {
					StarpowerSection = null;
					StarpowerMissEvent?.Invoke();
				}
			}
		}

		private int MaxMultiplier => (player.chosenInstrument == "bass" ? 6 : 4) * (starpowerActive ? 2 : 1);
		private int Multiplier => Mathf.Min((Combo / 10 + 1) * (starpowerActive ? 2 : 1), MaxMultiplier);

		private List<NoteInfo> Chart => Play.Instance.chart
			.GetChartByName(player.chosenInstrument)[player.chosenDifficulty];

		private bool _stopAudio = false;
		private bool StopAudio {
			set {
				if (value == _stopAudio) {
					return;
				}

				_stopAudio = value;

				if (!value) {
					Play.Instance.RaiseAudio(player.chosenInstrument);
				} else {
					Play.Instance.LowerAudio(player.chosenInstrument);
				}
			}
		}

		private int visualChartIndex = 0;
		private int realChartIndex = 0;
		private int eventChartIndex = 0;

		private Queue<List<NoteInfo>> expectedHits = new();
		private List<List<NoteInfo>> allowedOverstrums = new();
		private List<NoteInfo> heldNotes = new();

		private bool beat;

		private int notesHit = 0;

		private void Awake() {
			// Set up render texture
			var descriptor = new RenderTextureDescriptor(
				Screen.width, Screen.height,
				RenderTextureFormat.DefaultHDR
			);
			descriptor.mipCount = 0;
			var renderTexture = new RenderTexture(descriptor);
			trackCamera.targetTexture = renderTexture;

			// Set up camera
			var info = trackCamera.GetComponent<UniversalAdditionalCameraData>();
			if (GameManager.Instance.LowQualityMode) {
				info.antialiasing = AntialiasingMode.None;
			} else {
				info.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
				info.antialiasingQuality = AntialiasingQuality.Low;
			}
		}

		private void Start() {
			player.track = this;
			notePool.player = player;
			genericPool.player = player;

			// Inputs

			input = (FiveFretInputStrategy) player.inputStrategy;
			input.ResetForSong();

			input.FretChangeEvent += FretChangedAction;
			input.StrumEvent += StrumAction;
			input.StarpowerEvent += StarpowerAction;

			// Bind other events
			Play.Instance.BeatEvent += BeatAction;

			// Set render texture
			GameUI.Instance.AddTrackImage(trackCamera.targetTexture);

			// Spawn in frets
			for (int i = 0; i < 5; i++) {
				var fret = frets[i].GetComponent<Fret>();
				fret.SetColor(fretColors[i]);
				frets[i] = fret;
			}

			// Adjust hit window
			var scale = hitWindow.localScale;
			hitWindow.localScale = new(scale.x, Play.HIT_MARGIN * player.trackSpeed * 2f, scale.z);
		}

		private void OnDestroy() {
			// Release render texture
			trackCamera.targetTexture.Release();

			// Unbind input
			input.FretChangeEvent -= FretChangedAction;
			input.StrumEvent -= StrumAction;
			input.StarpowerEvent -= StarpowerAction;

			// Unbind other events
			Play.Instance.BeatEvent -= BeatAction;

			// Set score
			player.lastScore = new PlayerManager.Score {
				percentage = (float) notesHit / Chart.Count,
				notesHit = notesHit,
				notesMissed = Chart.Count - notesHit
			};
		}

		private void Update() {
			// Get chart stuff
			var events = Play.Instance.chart.events;

			// Update input strategy
			if (input.botMode) {
				input.UpdateBotMode(Chart, Play.Instance.SongTime);
			} else {
				input.UpdatePlayerMode();
			}

			UpdateMaterial();

			// Ignore everything else until the song starts
			if (!Play.Instance.SongStarted) {
				return;
			}

			// Update events (beat lines, starpower, etc.)
			while (events.Count > eventChartIndex && events[eventChartIndex].time <= RelativeTime) {
				var eventInfo = events[eventChartIndex];

				float compensation = TRACK_SPAWN_OFFSET - CalcLagCompensation(RelativeTime, eventInfo.time);
				if (eventInfo.name == "beatLine_minor") {
					genericPool.Add("beatLine_minor", new(0f, 0.01f, compensation));
				} else if (eventInfo.name == "beatLine_major") {
					genericPool.Add("beatLine_major", new(0f, 0.01f, compensation));
				} else if (eventInfo.name == $"starpower_{player.chosenInstrument}") {
					StarpowerSection = eventInfo;
				}

				eventChartIndex++;
			}

			// Since chart is sorted, this is guaranteed to work
			while (Chart.Count > visualChartIndex && Chart[visualChartIndex].time <= RelativeTime) {
				var noteInfo = Chart[visualChartIndex];

				SpawnNote(noteInfo, RelativeTime);
				visualChartIndex++;
			}

			// Update expected input
			while (Chart.Count > realChartIndex && Chart[realChartIndex].time <= Play.Instance.SongTime + Play.HIT_MARGIN) {
				var noteInfo = Chart[realChartIndex];

				var peeked = expectedHits.ReversePeekOrNull();
				if (peeked?[0].time == noteInfo.time) {
					// Add notes as chords
					peeked.Add(noteInfo);
				} else {
					// Or add notes as singular
					var l = new List<NoteInfo>(5) { noteInfo };
					expectedHits.Enqueue(l);
				}

				realChartIndex++;
			}

			// Update held notes
			for (int i = heldNotes.Count - 1; i >= 0; i--) {
				var heldNote = heldNotes[i];
				if (heldNote.time + heldNote.length <= Play.Instance.SongTime) {
					heldNotes.RemoveAt(i);
					frets[heldNote.fret].StopSustainParticles();
				}
			}

			// Update real input
			UpdateInput();

			// Update starpower region
			if (StarpowerSection?.EndTime + Play.HIT_MARGIN < Play.Instance.SongTime) {
				StarpowerSection = null;
				starpowerCharge += 0.25f;
			}

			// Update starpower active
			if (starpowerActive) {
				if (starpowerCharge <= 0f) {
					starpowerActive = false;
					starpowerCharge = 0f;
				} else {
					starpowerCharge -= Time.deltaTime / 25f;
				}
			}

			// Update info (combo, multiplier, etc.)
			UpdateInfo();

			// Un-strum
			strummed = false;
		}

		private void UpdateMaterial() {
			// Update track UV
			var trackMaterial = trackRenderer.material;
			var oldOffset = trackMaterial.GetVector("TexOffset");
			float movement = Time.deltaTime * player.trackSpeed / 4f;
			trackMaterial.SetVector("TexOffset", new(oldOffset.x, oldOffset.y - movement));

			// Update track groove
			float currentGroove = trackMaterial.GetFloat("GrooveState");
			if (Multiplier >= MaxMultiplier) {
				trackMaterial.SetFloat("GrooveState", Mathf.Lerp(currentGroove, 1f, Time.deltaTime * 5f));
			} else {
				trackMaterial.SetFloat("GrooveState", Mathf.Lerp(currentGroove, 0f, Time.deltaTime * 3f));
			}

			// Update track starpower
			float currentStarpower = trackMaterial.GetFloat("StarpowerState");
			if (starpowerActive) {
				trackMaterial.SetFloat("StarpowerState", Mathf.Lerp(currentStarpower, 1f, Time.deltaTime * 2f));
			} else {
				trackMaterial.SetFloat("StarpowerState", Mathf.Lerp(currentStarpower, 0f, Time.deltaTime * 4f));
			}

			// Update starpower bar
			var starpowerMat = starpowerBarTop.material;
			starpowerMat.SetFloat("Fill", starpowerCharge);
			if (beat) {
				float pulseAmount = 0f;
				if (starpowerActive) {
					pulseAmount = 0.25f;
				} else if (!starpowerActive && starpowerCharge >= 0.5f) {
					pulseAmount = 1f;
				}

				starpowerMat.SetFloat("Pulse", pulseAmount);
				beat = false;
			} else {
				float currentPulse = starpowerMat.GetFloat("Pulse");
				starpowerMat.SetFloat("Pulse", Mathf.Lerp(currentPulse, 0f, Time.deltaTime * 16f));
			}
		}

		private void UpdateInfo() {
			// Update text
			if (Multiplier == 1) {
				comboText.text = null;
			} else {
				comboText.text = $"{Multiplier}<sub>x</sub>";
			}

			// Update status

			int index = Combo % 10;
			if (Multiplier != 1 && index == 0) {
				index = 10;
			} else if (Multiplier == MaxMultiplier) {
				index = 10;
			}

			comboMeterRenderer.material.SetFloat("SpriteNum", index);
		}

		private void UpdateInput() {
			// Handle misses (multiple a frame in case of lag)
			while (Play.Instance.SongTime - expectedHits.PeekOrNull()?[0].time > Play.HIT_MARGIN) {
				var missedChord = expectedHits.Dequeue();

				// Call miss for each component
				Combo = 0;
				foreach (var hit in missedChord) {
					notePool.MissNote(hit);
					StopAudio = true;
				}
			}

			if (expectedHits.Count <= 0) {
				UpdateOverstrums();
				return;
			}

			// Handle hits (one per frame so no double hits)
			var chord = expectedHits.Peek();
			if (!chord[0].hopo && !strummed) {
				return;
			} else if (chord[0].hopo && Combo <= 0 && !strummed) {
				return;
			}

			// Check if correct chord is pressed
			if (!ChordPressed(chord)) {
				if (!chord[0].hopo) {
					Combo = 0;
				}

				return;
			}

			// If so, hit!
			expectedHits.Dequeue();

			Combo++;
			foreach (var hit in chord) {
				// Hit notes
				notePool.HitNote(hit);
				StopAudio = false;

				// Play particles
				frets[hit.fret].PlayParticles();

				// If sustained, add to held
				if (hit.length > 0.2f) {
					heldNotes.Add(hit);
					frets[hit.fret].PlaySustainParticles();
				}

				// Add stats
				notesHit++;
			}

			// If this is a tap note, and it was hit without strumming,
			// add it to the allowed overstrums. This is so the player
			// doesn't lose their combo when they strum AFTER they hit
			// the tap note.
			if (chord[0].hopo && !strummed) {
				allowedOverstrums.Add(chord);
			} else if (!chord[0].hopo) {
				allowedOverstrums.Clear();
			}
		}

		private void UpdateOverstrums() {
			// Remove all old allowed overstrums
			while (allowedOverstrums.Count > 0
				&& Play.Instance.SongTime - allowedOverstrums[0][0].time > Play.HIT_MARGIN) {

				allowedOverstrums.RemoveAt(0);
			}

			// Don't do anything else if we didn't strum
			if (!strummed) {
				return;
			}

			// Look in the allowed overstrums first
			for (int i = 0; i < allowedOverstrums.Count; i++) {
				if (ChordPressed(allowedOverstrums[i])) {
					// If we found a chord that was pressed, remove 
					// all of the allowed overstrums before it.
					// This prevents over-forgiving overstrums.

					for (int j = i; j >= 0; j--) {
						allowedOverstrums.RemoveAt(j);
					}

					// Overstrum forgiven!
					return;
				}
			}

			Combo = 0;

			// Let go of held notes
			for (int i = heldNotes.Count - 1; i >= 0; i--) {
				var heldNote = heldNotes[i];

				notePool.MissNote(heldNote);
				StopAudio = true;

				heldNotes.RemoveAt(i);
				frets[heldNote.fret].StopSustainParticles();
			}
		}

		private bool ChordPressed(List<NoteInfo> chordList) {
			// Convert NoteInfo list to chord fret array
			int[] chord = new int[chordList.Count];
			for (int i = 0; i < chord.Length; i++) {
				chord[i] = chordList[i].fret;
			}

			if (chord.Length == 1) {
				// Deal with single notes
				int fret = chord[0];
				for (int i = 0; i < frets.Length; i++) {
					// Skip any notes that are currently held down.
					// Extended sustains.
					if (heldNotes.Any(j => j.fret == i)) {
						continue;
					}

					if (frets[i].IsPressed && i > fret) {
						return false;
					} else if (!frets[i].IsPressed && i == fret) {
						return false;
					} else if (frets[i].IsPressed && i != fret && !Play.ANCHORING) {
						return false;
					}
				}
			} else {
				// Deal with multi-key chords
				for (int i = 0; i < frets.Length; i++) {
					// Skip any notes that are currently held down.
					// Extended sustains.
					if (heldNotes.Any(j => j.fret == i)) {
						continue;
					}

					bool contains = chord.Contains(i);
					if (contains && !frets[i].IsPressed) {
						return false;
					} else if (!contains && frets[i].IsPressed) {
						return false;
					}
				}
			}

			return true;
		}

		private void FretChangedAction(bool pressed, int fret) {
			frets[fret].SetPressed(pressed);

			if (!pressed) {
				// Let go of held notes
				NoteInfo letGo = null;
				for (int i = heldNotes.Count - 1; i >= 0; i--) {
					var heldNote = heldNotes[i];
					if (heldNote.fret != fret) {
						continue;
					}

					notePool.MissNote(heldNote);
					heldNotes.RemoveAt(i);
					frets[heldNote.fret].StopSustainParticles();

					letGo = heldNote;
				}

				// Only stop audio if all notes were let go and...
				if (letGo != null && heldNotes.Count <= 0) {
					// ...if the player let go of the note more than 0.15s
					// before the end of the note. This prevents the game
					// from stopping the audio if the player let go a handful
					// of milliseconds too early (which is okay).
					float endTime = letGo.time + letGo.length;
					if (endTime - Play.Instance.SongTime > 0.15f) {
						StopAudio = true;
					}
				}
			}
		}

		private void StrumAction() {
			strummed = true;
		}

		private void StarpowerAction() {
			if (!starpowerActive && starpowerCharge >= 0.5f) {
				starpowerActive = true;
			}
		}

		private void BeatAction() {
			beat = true;
		}

		private void SpawnNote(NoteInfo noteInfo, float time) {
			// Set correct position
			float lagCompensation = CalcLagCompensation(time, noteInfo.time);
			float x = frets[noteInfo.fret].transform.localPosition.x;
			var pos = new Vector3(x, 0f, TRACK_SPAWN_OFFSET - lagCompensation);

			// Get color

			// Set note info
			var noteComp = notePool.CreateNote(noteInfo, pos);
			noteComp.SetInfo(fretColors[noteInfo.fret], noteInfo.length, noteInfo.hopo);
		}

		private float CalcLagCompensation(float currentTime, float noteTime) {
			return (currentTime - noteTime) * player.trackSpeed;
		}
	}
}