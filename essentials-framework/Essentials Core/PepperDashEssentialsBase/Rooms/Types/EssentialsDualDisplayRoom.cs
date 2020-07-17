﻿using System;
using System.Collections.Generic;
using System.Linq;
using PepperDash.Core;
using PepperDash.Essentials.Core;
using PepperDash.Essentials.Core.Config;
using PepperDash.Essentials.Core.Rooms.Config;
using PepperDash_Essentials_Core.Devices;

namespace PepperDash.Essentials
{
    public class EssentialsDualDisplayRoom : EssentialsHuddleVtc1Room
    {
        public const string DefaultDestinationListKey = "default";
        private const string LeftDestinationKey = "leftDisplay";
        private const string RightDestinationKey = "rightDisplay";

        public EssentialsDualDisplayRoomPropertiesConfig RoomConfig { get; private set; }

        public EAudioBehavior AudioRoutingBehavior { get; set; }
        public EVideoBehavior VideoRoutingBehavior { get; set; }

        private string _destinationListKey;

        public EssentialsDualDisplayRoom(DeviceConfig config) : base(config)
        {
            RoomConfig = config.Properties.ToObject<EssentialsDualDisplayRoomPropertiesConfig>();

            Initialize();
        }

        public Dictionary<string, DestinationListItem> DestinationList { get; private set; }

        public BoolFeedback LeftDisplayIsWarmingUpFeedback { get; private set; }
        public BoolFeedback RightDisplayIsWarmingUpFeedback { get; private set; }
        public BoolFeedback LeftDisplayIsCoolingDownFeedback { get; private set; }
        public BoolFeedback RightDisplayIsCoolingDownFeedback { get; private set; }

        public IRoutingSinkWithSwitching LeftDisplay { get; private set; }
        public IRoutingSinkWithSwitching RightDisplay { get; private set; }

        private void Initialize()
        {
            try
            {
                _destinationListKey = String.IsNullOrEmpty(RoomConfig.DestinationListKey)
                    ? DefaultDestinationListKey
                    : RoomConfig.DestinationListKey;

                AudioRoutingBehavior = RoomConfig.DefaultAudioBehavior;
                VideoRoutingBehavior = RoomConfig.DefaultVideoBehavior;

                InitializeDestinations();
            }
            catch (Exception e)
            {
                Debug.Console(0, this, "Error Initializing Room: {0}", e);
            }
        }

        private void InitializeDestinations()
        {
            DestinationList = ConfigReader.ConfigObject.GetDestinationListForKey(_destinationListKey);

            if (DestinationList == null)
            {
                Debug.Console(0, Debug.ErrorLogLevel.Error, "No destination list with key {0} found",
                    _destinationListKey);
                return;
            }

            //left destination is defined as the display on the 0 surface, at location 0,0 (h, v)
            var leftDest = GetDestinationForKey(LeftDestinationKey);

            //not found by key, check by expected location
            if (leftDest == null)
            {
                DestinationList.Values.FirstOrDefault(
                    li => li.SurfaceLocation == 0 && li.HorizontalLocation == 0 && li.VerticalLocation == 0);
            }

            //right destination is defined as the display on the 0 surface, at location 0,0 (h, v)
            var rightDest = GetDestinationForKey(RightDestinationKey);

            //not found by key, check by expected location
            if (rightDest == null)
            {
                DestinationList.Values.FirstOrDefault(
                    li => li.SurfaceLocation == 0 && li.HorizontalLocation == 1 && li.VerticalLocation == 0);
            }

            if (leftDest == null || rightDest == null)
            {
                Debug.Console(0, Debug.ErrorLogLevel.Error, "Dual destinations not defined. Please check configuration");
                return;
            }

            var leftDisplay = leftDest.SinkDevice as DisplayBase;
            var rightDisplay = rightDest.SinkDevice as DisplayBase;

            if (leftDisplay == null || rightDisplay == null)
            {
                Debug.Console(0, Debug.ErrorLogLevel.Error,
                    "Display for key {0} && key {1} not found. Please check configurattion");
                Debug.Console(0, Debug.ErrorLogLevel.Error, "LeftDisplay: {0}\r\nRightDisplay: {1}", leftDest.SinkKey,
                    rightDest.SinkKey);

                return;
            }

            //need displays as DisplayBase later instead of IRoutingSinkWithSwtich
            LeftDisplay = leftDisplay;
            RightDisplay = rightDisplay;

            //TODO: Check this definition for on for dual display rooms
            OnFeedbackFunc = () => CurrentSourceInfo != null && CurrentSourceInfo.Type == eSourceListItemType.Route;

            IsWarmingFeedbackFunc =
                () => leftDisplay.IsWarmingUpFeedback.BoolValue || rightDisplay.IsWarmingUpFeedback.BoolValue;

            IsCoolingFeedbackFunc = () => leftDisplay.IsWarmingUpFeedback.BoolValue ||
                                          rightDisplay.IsCoolingDownFeedback.BoolValue;

            LeftDisplayIsWarmingUpFeedback = new BoolFeedback(() => leftDisplay.IsWarmingUpFeedback.BoolValue);
            LeftDisplayIsCoolingDownFeedback = new BoolFeedback(() => leftDisplay.IsCoolingDownFeedback.BoolValue);


            RightDisplayIsWarmingUpFeedback = new BoolFeedback(() => rightDisplay.IsWarmingUpFeedback.BoolValue);
            RightDisplayIsCoolingDownFeedback = new BoolFeedback(() => rightDisplay.IsCoolingDownFeedback.BoolValue);

            InitializeDisplay(leftDisplay);
            InitializeDisplay(rightDisplay);
        }

        private DestinationListItem GetDestinationForKey(string key)
        {
            DestinationListItem returnValue;

            DestinationList.TryGetValue(key, out returnValue);

            return returnValue;
        }


        protected override void IsCoolingDownFeedbackOnOutputChange(object sender, FeedbackEventArgs args)
        {
            IsCoolingDownFeedback.FireUpdate();
            LeftDisplayIsCoolingDownFeedback.FireUpdate();
            RightDisplayIsCoolingDownFeedback.FireUpdate();
        }

        public override void RoomVacatedForTimeoutPeriod(object o)
        {
            throw new NotImplementedException();
        }


        protected override void IsWarmingUpFeedbackOnOutputChange(object sender, FeedbackEventArgs args)
        {
            IsWarmingUpFeedback.FireUpdate();
            LeftDisplayIsWarmingUpFeedback.FireUpdate();
            RightDisplayIsWarmingUpFeedback.FireUpdate();
        }

        protected override void PowerIsOnFeedbackOnOutputChange(object sender, FeedbackEventArgs args)
        {
            var ld = LeftDisplay as DisplayBase;
            var rd = RightDisplay as DisplayBase;

            if (ld == null || rd == null)
            {
                return;
            }

            //if room is already on and either display is still on, no need to fire update
            if (OnFeedback.BoolValue && (ld.PowerIsOnFeedback.BoolValue || rd.PowerIsOnFeedback.BoolValue))
            {
                return;
            }

            //if both displays are off, room is off, clear the current source
            if (!ld.PowerIsOnFeedback.BoolValue && !rd.PowerIsOnFeedback.BoolValue)
            {
                CurrentSourceInfo = null;
            }

            OnFeedback.FireUpdate();
        }

        public void RouteAction(string sourceKey, string destinationKey)
        {
            var routeItem = new SourceRouteListItem
            {
                DestinationKey = destinationKey,
                SourceKey = sourceKey,
                Type = eRoutingSignalType.AudioVideo
            };

            DoRoute(routeItem);
        }

        public void SelectSource(string sourceKey, string sourceListKey)
        {
            var srcList = GetSourceListForKey(sourceKey, sourceListKey);

            var src = srcList[sourceKey];

            if (src.SourceKey != "roomoff")
            {
                LastSourceKey = sourceKey;
            }
            else
            {
                CurrentSourceInfoKey = null;
            }

            SetVolumeControl(src);

            if (src.SourceKey == "$off")
            {
                CurrentSourceInfo = null;
            } else if (src.SourceKey != null)
            {
                CurrentSourceInfo = src;
            }

            OnFeedback.FireUpdate();
        }
    }
}