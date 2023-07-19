﻿using System;
using UnityEngine;
using YARG.Core.Chart;
using YARG.Gameplay.Player;
using YARG.Settings.ColorProfiles;

namespace YARG.Gameplay.Visuals
{
    public sealed class FiveFretNoteElement : NoteElement<GuitarNote, FiveFretPlayer>
    {
        [SerializeField]
        private NoteGroup _strumGroup;

        [SerializeField]
        private NoteGroup _hopoGroup;

        [SerializeField]
        private NoteGroup _tapGroup;

        protected override void InitializeElement()
        {
            transform.localPosition = new Vector3(
                BasePlayer.TRACK_WIDTH / 5f * NoteRef.Fret - BasePlayer.TRACK_WIDTH / 2f - 1f / 5f,
                0f, 0f);

            // Get which note model to use
            NoteGroup = NoteRef.Type switch
            {
                GuitarNoteType.Strum => _strumGroup,
                GuitarNoteType.Hopo  => _hopoGroup,
                GuitarNoteType.Tap   => _tapGroup,
                _                    => throw new ArgumentOutOfRangeException(nameof(NoteRef.Type))
            };

            // Show and set material properties
            NoteGroup.SetActive(true);
            // TODO: Note material seed

            // Set note color
            UpdateColor();
        }

        protected override void HideElement()
        {
            _strumGroup.SetActive(false);
            _hopoGroup.SetActive(false);
            _tapGroup.SetActive(false);
        }

        protected override void UpdateElement()
        {
            base.UpdateElement();

            UpdateColor();
        }

        private void UpdateColor()
        {
            // Get which note color to use
            var color = NoteRef.IsStarPower
                ? ColorProfile.Default.FiveFret.StarpowerNoteColor
                : ColorProfile.Default.FiveFret.NoteColors[NoteRef.Fret];

            // Set the color
            NoteGroup.ColoredMaterial.color = color;
        }
    }
}