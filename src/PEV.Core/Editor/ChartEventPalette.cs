using System;
using System.Collections.Generic;
using System.Linq;
using ADOFAI;
using ADOFAI.Editor;
using HarmonyLib;
using PEV.Registry;
using UnityEngine;

namespace PEV.Editor
{
    /// <summary>Updates only package-owned controls when a different chart is opened.</summary>
    internal static class ChartEventPalette
    {
        internal static void Clear(scnEditor editor)
        {
            if (editor == null) return;
            var inspector = editor.levelEventsPanel;
            if (inspector != null && inspector.panelsList != null)
            {
                foreach (var panel in inspector.panelsList.ToArray())
                {
                    if (panel == null) continue;
                    if (EventRegistry.TryGetByType(panel.levelEventType, out _))
                    {
                        inspector.panelsList.Remove(panel);
                        if (panel.tabContainer != null) HideAndDestroy(panel.tabContainer.gameObject);
                        HideAndDestroy(panel.gameObject);
                    }
                    else if (panel.levelEventType == LevelEventType.AddDecoration)
                    {
                        foreach (string name in DecorationParams.InjectedNames.ToArray())
                        {
                            if (!panel.properties.TryGetValue(name, out var property)) continue;
                            panel.properties.Remove(name);
                            panel.propertySelectables.RemoveAll(item => item.propertyRef == property);
                            if (property != null) HideAndDestroy(property.gameObject);
                        }
                    }
                }
                if (inspector.tabs != null)
                    foreach (var tab in inspector.tabs.GetComponentsInChildren<InspectorTab>(true))
                        if (EventRegistry.TryGetByType(tab.levelEventType, out _)) HideAndDestroy(tab.gameObject);
            }
            foreach (var list in editor.eventButtons.Values)
                for (int i = list.Count - 1; i >= 0; i--)
                    if (list[i] != null && EventRegistry.TryGetByType(list[i].type, out _))
                    { var button = list[i]; list.RemoveAt(i); HideAndDestroy(button.gameObject); }
        }

        internal static void Publish(scnEditor editor)
        {
            if (editor == null || editor.levelEventsPanel?.panelsList == null || editor.eventButtons == null) return;
            EventRegistry.EnsureAllIcons();
            DecorationParams.Publish();
            var inspector = editor.levelEventsPanel;
            var native = inspector.panelsList.FirstOrDefault(p => p != null && p.levelEventType == LevelEventType.AddDecoration);
            if (native != null && DecorationParams.TryGetNativeInfo(out var decorationInfo))
                foreach (string name in DecorationParams.InjectedNames)
                    if (!native.properties.ContainsKey(name) && decorationInfo.propertiesInfo.TryGetValue(name, out var spec))
                        native.RenderControl(name, spec);

            var categories = AccessTools.Field(typeof(scnEditor), "categoryTabs")?.GetValue(editor) as List<CategoryTab>;
            foreach (var manifest in EventRegistry.All)
            {
                var type = (LevelEventType)PEV.Util.StableId.FromEventId(manifest.Id);
                if (!EventRegistry.TryGetInfo(type, out var info)) continue;
                foreach (var category in info.categories)
                {
                    if (!editor.eventButtons.TryGetValue(category, out var list)) continue;
                    if (categories != null && !categories.Any(tab => tab != null && tab.levelEventCategory == category))
                    {
                        var categoryObject = UnityEngine.Object.Instantiate(editor.prefab_eventCategoryTab, editor.levelEventsBarCategories);
                        var categoryRect = categoryObject.GetComponent<RectTransform>();
                        var position = categoryRect.anchoredPosition; position.y = 55f; categoryRect.anchoredPosition = position;
                        var categoryTab = categoryObject.GetComponent<CategoryTab>(); categoryTab.Init(category); categories.Add(categoryTab);
                    }
                    int index = list.Count;
                    var buttonObject = UnityEngine.Object.Instantiate(editor.prefab_levelEventButton, editor.levelEventsBarButtons);
                    var rect = buttonObject.GetComponent<RectTransform>();
                    var position2 = rect.anchoredPosition; position2.x = rect.sizeDelta.x * (index % 11); rect.anchoredPosition = position2;
                    var button = buttonObject.GetComponent<LevelEventButton>(); button.Init(type, index / 11, index % 11 + 1);
                    list.Add(button);
                }
                AddInspectorPanel(inspector, type, info);
            }
            editor.SetupFavoritesCategory();
        }

        private static void AddInspectorPanel(InspectorPanel inspector, LevelEventType type, LevelEventInfo info)
        {
            var panelObject = UnityEngine.Object.Instantiate(ADOBase.gc.prefab_propertiesPanel);
            panelObject.transform.SetParent(inspector.panels, false); panelObject.name = ((int)type).ToString();
            var panel = panelObject.GetComponent<PropertiesPanel>(); panel.levelEventType = type;
            inspector.panelsList.Add(panel); panelObject.SetActive(false);
            var tabObject = UnityEngine.Object.Instantiate(ADOBase.gc.prefab_tab);
            tabObject.transform.SetParent(inspector.tabs, false);
            var tab = tabObject.GetComponent<InspectorTab>(); tab.Init(type, inspector);
            var tabRect = tab.GetComponent<RectTransform>(); var position = tabRect.anchoredPosition;
            position.y = -68f * (inspector.panelsList.Count - 1); tabRect.anchoredPosition = position;
            tab.SetSelected(false); panel.Init(inspector, info);
            if (!info.useGroups) return;
            inspector.subTabButtonTemplate.gameObject.SetActive(false);
            var container = UnityEngine.Object.Instantiate(inspector.subTabGroupTemplate, inspector.subTabButtons);
            container.gameObject.name = info.name; panel.tabContainer = container;
            foreach (var group in info.groups)
            {
                var button = UnityEngine.Object.Instantiate(inspector.subTabButtonTemplate, container);
                button.gameObject.name = group.name;
                button.SetIcon(Resources.Load<Sprite>("LevelEditor/GroupIcons/" + group.icon));
                button.gameObject.SetActive(true); button.groupName = group.name;
                string name = group.name; button.onClick.AddListener(() => panel.SelectTab(name));
                panel.tabButtons.Add(group.name, button);
            }
        }
        private static void HideAndDestroy(GameObject go) { if (go != null) { go.SetActive(false); UnityEngine.Object.Destroy(go); } }
    }
}
