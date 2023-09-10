﻿#nullable enable

using System;
using System.Linq;
using UnityEngine.Device;

namespace Core.Config.SystemConfigCategories
{
	[SettingsCategory("graphics", "Graphics", CommonSettingsCategorySections.Hardware)]
	public class GraphicsSettings : SettingsCategory
	{
		public string? DisplayResolution
		{
			get => GetValue(nameof(DisplayResolution), null);
			set => SetValue(nameof(DisplayResolution), value);
		}
		
		public bool Fullscreen
		{
			get => GetValue(nameof(Fullscreen), true);
			set => SetValue(nameof(Fullscreen), value);
		}
		
		public bool VSync
		{
			get => GetValue(nameof(VSync), true);
			set => SetValue(nameof(VSync), value);
		}
		
		/// <inheritdoc />
		public GraphicsSettings(ISettingsManager settingsManager) : base(settingsManager)
		{
			
		}

		private string[] GetAvailableResolutions()
		{
			#if UNITY_EDITOR
			return GetEditorResolution();
			#else
			return Screen.resolutions
				.OrderByDescending(x => x.height)
				.Select(x => $"{x.width}x{x.height}")
				.Distinct()
				.ToArray();
			#endif
		}
		
		/// <inheritdoc />
		public override void BuildSettingsUi(ISettingsUiBuilder uiBuilder)
		{
			string[] resolutions = GetAvailableResolutions();
			int currentIndex;
				
#if UNITY_EDITOR
			currentIndex = 0;
#else
			currentIndex = Array.IndexOf(resolutions, DisplayResolution);
			
			if (currentIndex == -1)
				currentIndex = 0;
#endif

			uiBuilder.AddSection("Display", out int display)
				.WithStringDropdown(
					"Resolution",
					null,
					currentIndex,
					resolutions,
					x => DisplayResolution = resolutions[x],
					display
				).WithToggle(
					"Fullscreen",
					null,
					Fullscreen,
					x => Fullscreen = x,
					display
				).WithToggle(
					"Enable V-sync",
					null,
					VSync,
					x => VSync = x,
					display
				);
			
			
		}
		
		#if UNITY_EDITOR

		private string[] GetEditorResolution()
		{
			return new[]
			{
				$"{Screen.width}x{Screen.height}"
			};
		}
		
		#endif
	}
}