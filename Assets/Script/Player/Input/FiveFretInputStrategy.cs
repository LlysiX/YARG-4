using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using YARG.Data;
using YARG.Player.Navigation;
using YARG.PlayMode;

namespace YARG.Player.Input
{
    public class FiveFretInputStrategy : InputStrategy
    {
        public const string GREEN = "green";
        public const string RED = "red";
        public const string YELLOW = "yellow";
        public const string BLUE = "blue";
        public const string ORANGE = "orange";

        public const string STRUM_UP = "strum_up";
        public const string STRUM_DOWN = "strum_down";

        public const string WHAMMY = "whammy";

        public const string STAR_POWER = "star_power";
        public const string TILT = "tilt";
        public const string PAUSE = "pause";
        public delegate void FretChangeAction(bool pressed, int fret);

        public event FretChangeAction FretChangeEvent;

        public event Action StrumEvent;

        public delegate void WhammyChangeAction(float delta);

        public event WhammyChangeAction WhammyEvent;

        public FiveFretInputStrategy(IReadOnlyList<InputDevice> inputDevices) : base(inputDevices)
        {
            InputMappings = new()
            {
                { GREEN,      new(BindingType.BUTTON, "Green",      GREEN) },
                { RED,        new(BindingType.BUTTON, "Red",        RED) },
                { YELLOW,     new(BindingType.BUTTON, "Yellow",     YELLOW) },
                { BLUE,       new(BindingType.BUTTON, "Blue",       BLUE) },
                { ORANGE,     new(BindingType.BUTTON, "Orange",     ORANGE) },
                { STRUM_UP,   new(BindingType.BUTTON, "Strum Up",   STRUM_UP, STRUM_DOWN) },
                { STRUM_DOWN, new(BindingType.BUTTON, "Strum Down", STRUM_DOWN, STRUM_UP) },
                { WHAMMY,     new(BindingType.AXIS,   "Whammy",     WHAMMY) },
                { STAR_POWER, new(BindingType.BUTTON, "Star Power", STAR_POWER) },
                // tilt is a button as PS2 guitars don't have a tilt axis
                { TILT,       new(BindingType.BUTTON, "Tilt",       TILT) },
                { PAUSE,      new(BindingType.BUTTON, "Pause",      PAUSE) },
            };
        }

        public override string GetIconName()
        {
            return "guitar";
        }

        protected override void UpdatePlayerMode()
        {
            void HandleFret(string mapping, int index)
            {
                if (WasMappingPressed(mapping))
                {
                    FretChangeEvent?.Invoke(true, index);
                }
                else if (WasMappingReleased(mapping))
                {
                    FretChangeEvent?.Invoke(false, index);
                }
            }

            // Deal with fret inputs

            HandleFret(GREEN, 0);
            HandleFret(RED, 1);
            HandleFret(YELLOW, 2);
            HandleFret(BLUE, 3);
            HandleFret(ORANGE, 4);

            // Deal with strumming

            if (WasMappingPressed(STRUM_UP))
            {
                StrumEvent?.Invoke();
                CallGenericCalbirationEvent();
            }

            if (WasMappingPressed(STRUM_DOWN))
            {
                StrumEvent?.Invoke();
                CallGenericCalbirationEvent();
            }

            // Whammy!
            float currentWhammy = GetMappingValue(WHAMMY);
            float deltaWhammy = currentWhammy - GetPreviousMappingValue(WHAMMY);
            if (!Mathf.Approximately(deltaWhammy, 0f))
            {
                WhammyEvent?.Invoke(deltaWhammy);
            }

            // Starpower

            if (WasMappingPressed(STAR_POWER) || WasMappingPressed(TILT))
            {
                // checking for tilt
                CallStarpowerEvent();
            }
        }

        protected override void UpdateNavigationMode()
        {
            NavigationEventForMapping(MenuAction.Confirm, GREEN);
            NavigationEventForMapping(MenuAction.Back, RED);

            NavigationEventForMapping(MenuAction.Shortcut1, YELLOW);
            NavigationEventForMapping(MenuAction.Shortcut2, BLUE);
            NavigationHoldableForMapping(MenuAction.Shortcut3, ORANGE);

            NavigationHoldableForMapping(MenuAction.Up, STRUM_UP);
            NavigationHoldableForMapping(MenuAction.Down, STRUM_DOWN);

            NavigationEventForMapping(MenuAction.More, STAR_POWER);

            if (WasMappingPressed(PAUSE))
            {
                CallPauseEvent();
            }
        }

        public override Instrument[] GetAllowedInstruments()
        {
            return new Instrument[]
            {
                Instrument.GUITAR, Instrument.BASS, Instrument.KEYS, Instrument.GUITAR_COOP, Instrument.RHYTHM,
            };
        }

        public override string GetTrackPath()
        {
            return "Tracks/FiveFret";
        }
    }
}