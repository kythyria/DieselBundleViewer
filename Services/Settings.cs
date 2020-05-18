﻿using AdonisUI.Controls;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace DieselBundleViewer.Services
{
    public class SettingsData
    {
        public bool DisplayEmptyFiles = false;
        public bool ExtractFullDir = false;
    }

    public static class Settings
    {
        private const string FILE = "./Settings.xml";
        public static SettingsData Data { get; set; }

        static Settings()
        {
            Console.WriteLine("Loading setting...");
            ReadSettings();
        }

        public static void ReadSettings()
        {
            if (!File.Exists(FILE))
            {
                File.Create(FILE).Close();
                SaveSettings();
            } else
            {
                XmlSerializer xml = new XmlSerializer(typeof(SettingsData));
                try
                {
                    using var fs = new FileStream(FILE, FileMode.Open, FileAccess.Read);
                    Data = (SettingsData)xml.Deserialize(fs);
                } catch (InvalidOperationException e)
                {
                    Console.WriteLine("Error while reading settings file: "+e.Message);
                }
                if(Data == null)
                {
                    Console.WriteLine("Settings file is corrupted. Creating a new one.");
                    SaveSettings();
                }
            }
        }

        public static void SaveSettings()
        {
            Data ??= new SettingsData();
            if (!File.Exists(FILE))
                File.Create(FILE).Close();

            XmlSerializer xml = new XmlSerializer(typeof(SettingsData));
            using var fs = new FileStream(FILE, FileMode.Open, FileAccess.Write);
            xml.Serialize(fs, Data);
            Console.WriteLine("Saved settings");
        }
    }
}