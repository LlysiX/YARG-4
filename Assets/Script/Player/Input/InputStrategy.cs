using System;
using System.Collections.Generic;
using System.Linq;
using PlasticBand.Haptics;
using TMPro;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem.Utilities;
using YARG.Audio;
using YARG.Data;
using YARG.Player.Navigation;
using YARG.Settings;

namespace YARG.Player.Input
{
    public abstract class InputStrategy : IDisposable
    {
        // private ISantrollerHaptics _haptics;

        public IReadOnlyList<InputDevice> InputDevices { get; private set; }

        /// <summary>
        /// A list of the controls that correspond to each mapping.
        /// </summary>
        protected Dictionary<string, ControlBinding> InputMappings = new();

        private readonly List<IDisposable> _eventListeners = new();

        public IReadOnlyDictionary<string, ControlBinding> Mappings => InputMappings;

        /// <summary>
        /// Gets invoked when the button for generic calibration is pressed.<br/>
        /// Make sure <see cref="UpdatePlayerMode"/> is being called.
        /// </summary>
        public event Action<InputStrategy> GenericCalibrationEvent;

        /// <summary>
        /// Gets invoked when the button for generic starpower is pressed.
        /// </summary>
        public event Action<InputStrategy> StarpowerEvent;

        /// <summary>
        /// Gets invoked when the button for generic pause is pressed.
        /// </summary>
        public event Action PauseEvent;

        public InputStrategy(IReadOnlyList<InputDevice> inputDevices)
        {
            InputDevices = inputDevices;

            // Bind all events from the input device(s)
            InputSystem.onAfterUpdate += OnUpdate;
            foreach (var inputDevice in InputDevices)
            {
                var listener = InputSystem.onEvent.ForDevice(inputDevice).Call(OnInputEvent);
                _eventListeners.Add(listener);
            }

            // Set up debounce overrides
            foreach (var mapping in InputMappings.Values)
            {
                string overrideKey = mapping.DebounceOverrideKey;
                if (overrideKey != null && InputMappings.TryGetValue(overrideKey, out var overrideMapping))
                {
                    mapping.DebounceOverrideBinding = overrideMapping;
                }
            }
        }

        public void Dispose()
        {
            InputSystem.onAfterUpdate -= OnUpdate;
            foreach (var eventListener in _eventListeners)
            {
                eventListener?.Dispose();
            }
        }

        /// <returns>
        /// The name of the icon to show in players menu edition
        /// </returns>
        public abstract string GetIconName();

        /// <returns>
        /// An array of the allow instruments for the input strategy.
        /// </returns>
        public abstract Instrument[] GetAllowedInstruments();

        /// <returns>
        /// The path of the track addressable.
        /// </returns>
        public abstract string GetTrackPath();

        /// <summary>
        /// Resets the InputStrategy for a new song.
        /// </summary>
        public virtual void ResetForSong()
        {
        }

        /// <summary>
        /// Updates the player mode (normal mode) for this particular InputStrategy.
        /// </summary>
        protected abstract void UpdatePlayerMode();

        /// <summary>
        /// Updates the navigation mode (menu mode) for this particular InputStrategy.
        /// </summary>
        protected abstract void UpdateNavigationMode();

        protected void CallStarpowerEvent()
        {
            StarpowerEvent?.Invoke(this);
        }

        protected void CallPauseEvent()
        {
            PauseEvent?.Invoke();
        }

        protected void CallGenericCalbirationEvent()
        {
            GenericCalibrationEvent?.Invoke(this);
        }

        protected virtual void OnUpdate()
        {
            // Update mapping debouncing
            bool stateUpdated = false;
            foreach (var mapping in InputMappings.Values)
            {
                stateUpdated |= mapping.UpdateDebounce();
            }

            // Update inputs if necessary
            if (stateUpdated)
            {
                UpdateNavigationMode();
                UpdatePlayerMode();
            }
        }

        private void OnInputEvent(InputEventPtr eventPtr)
        {
            // Only take state events
            if (!eventPtr.IsA<StateEvent>() && !eventPtr.IsA<DeltaStateEvent>())
            {
                return;
            }

            // Ignore navigation events from the keyboard while a text box is selected
            // We detect whether a text box is selected by seeing if a focused input field is a component of the currently selected object
            if (eventPtr.deviceId == Keyboard.current.deviceId &&
                (EventSystem.current.currentSelectedGameObject?.GetComponents<TMP_InputField>().Any(i => i.isFocused) ??
                    false))
            {
                return;
            }

            // Update mapping states
            foreach (var mapping in InputMappings.Values)
            {
                mapping.UpdateState(eventPtr);
            }

            // Update inputs
            UpdateNavigationMode();
            UpdatePlayerMode();
        }

        /// <summary>
        /// Forces the input strategy to update.
        /// </summary>
        public void ForceUpdate()
        {
            UpdateNavigationMode();
            UpdatePlayerMode();
            OnUpdate();
        }

        public static bool IsControlPressed(InputControl<float> control)
        {
            if (control is ButtonControl button)
            {
                return button.isPressed;
            }

            return control.IsActuated(SettingsManager.Settings.PressThreshold.Data);
        }

        public static bool IsControlPressed(InputControl<float> control, InputEventPtr eventPtr)
        {
            if (control is ButtonControl button)
            {
                return button.IsValueConsideredPressed(button.ReadValueFromEvent(eventPtr));
            }

            return control.ReadValueFromEvent(eventPtr) >= SettingsManager.Settings.PressThreshold.Data;
        }

        protected bool IsMappingPressed(string key)
        {
            return InputMappings[key].IsPressed();
        }

        protected bool WasMappingPressed(string key)
        {
            return InputMappings[key].WasPressed();
        }

        protected bool WasMappingReleased(string key)
        {
            return InputMappings[key].WasReleased();
        }

        protected float GetMappingValue(string key)
        {
            return InputMappings[key].State.current;
        }

        protected float GetPreviousMappingValue(string key)
        {
            return InputMappings[key].State.previous;
        }

        public InputControl<float> GetMappingInputControl(string name)
        {
            return InputMappings[name].Control;
        }

        public void SetMappingInputControl(string name, InputControl<float> control)
        {
            InputMappings[name].Control = control;
        }

        protected void NavigationEventForMapping(MenuAction action, string mapping)
        {
            if (WasMappingPressed(mapping))
            {
                Navigator.Instance.CallNavigationEvent(action, this);
            }
        }

        protected void NavigationHoldableForMapping(MenuAction action, string mapping)
        {
            if (WasMappingPressed(mapping))
            {
                Navigator.Instance.StartNavigationHold(action, this);
            }
            else if (WasMappingReleased(mapping))
            {
                Navigator.Instance.EndNavigationHold(action, this);
            }
        }

        // public void SendStarPowerFill(float fill) => _haptics?.SetStarPowerFill(fill);
        //
        // public void SendStarPowerActive(bool enabled) => _haptics?.SetStarPowerActive(enabled);
        //
        // public void SendMultiplier(uint multiplier) => _haptics?.SetMultiplier(multiplier);
        //
        // public void SendSolo(bool enabled) => _haptics?.SetSolo(enabled);
        //
        // public virtual void ResetHaptics() => _haptics?.ResetHaptics();
    }
}