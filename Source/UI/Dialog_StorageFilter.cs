using UnityEngine;
using Verse;
using DigitalStorage.Components;

namespace DigitalStorage.UI
{
    public class Dialog_StorageFilter : Window
    {
        private readonly Building_StorageCore core;
        private ThingFilterUI.UIState uiState = new ThingFilterUI.UIState();

        public override Vector2 InitialSize => new Vector2(300f, 480f);

        public Dialog_StorageFilter(Building_StorageCore core)
        {
            this.core = core;
            doCloseX = true;
            absorbInputAroundWindow = false;
            closeOnClickedOutside = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            ThingFilterUI.DoThingFilterConfigWindow(inRect, uiState, core.StorageFilter, core.GetParentFilterPublic(), 8);
        }
    }
}
