using BepInEx;
using RoR2;
using R2API.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;

using NAudio.Wave;
using NAudio.Wave.SampleProviders;

using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using System.Reflection;
using System.Globalization;

namespace OriginalSoundTrack {
    // The OriginalSoundTrack plugin - For replacing the in game music with Risk of Rain 1 music (or your own).
    // You will need access to your own risk of rain 1 sound files (or any others you want to use).
    // Edit the settings.xml to specify how the music plays in game.
    // Also don't forget to mute the in game music in the in game settings (this plugin doesn't take away RoR2 music).

    //This attribute specifies that we have a dependency on R2API, as we're using it to add Bandit to the game.
    //You don't need this if you're not using R2API in your plugin, it's just to tell BepInEx to initialize R2API before this plugin so it's safe to use R2API.
    [BepInDependency("com.bepis.r2api")]

    //This attribute is required, and lists metadata for your plugin.
    //The GUID should be a unique ID for this plugin, which is human readable (as it is used in places like the config). I like to use the java package notation, which is "com.[your name here].[your plugin name here]"
    //The name is the name of the plugin that's displayed on load, and the version number just specifies what version the plugin is.
    [BepInPlugin("com.kylepaulsen.originalsoundtrack", "OriginalSoundTrack", "1.2.0")]

    [NetworkCompatibility(CompatibilityLevel.NoNeedForSync, VersionStrictness.DifferentModVersionsAreOk)]

    //This is the main declaration of our plugin class. BepInEx searches for all classes inheriting from BaseUnityPlugin to initialize on startup.
    //BaseUnityPlugin itself inherits from MonoBehaviour, so you can use this as a reference for what you can declare and use in your plugin class: https://docs.unity3d.com/ScriptReference/MonoBehaviour.html
    public class OriginalSoundTrack : BaseUnityPlugin {

        private FadeInOutSampleProvider fader;
        private WaveOutEvent outputDevice;

        private FileInfo[] soundFiles; // array of files found in the plugin folder
        private List<Music> musics = new List<Music>(); // list of main music objects.
        private AudioFileReader currentSong;
        private string currentSongFullName = null; // helpful for not restarting a song when it's already playing.
        private bool startedTeleporterEvent = false; // tracks the first interaction with the tele.
        private bool songPaused = false; // for pausing the music when the player pauses.
        private float globalMusicVolume = 0.5f; // default global music volume.
        private bool shouldLoop = true; // should songs loop when they end?
        private bool shouldPool = false; // should include songs from normal soundtrack?
        private string oldMusicVolume = ""; // what the music convar was before we override it.
        private string currentScene = ""; // helpful for picking out boss music.
        private System.Random rnd = new System.Random(); // helpful for picking random music.
        private XmlElement settings; // settings data.

        //The Awake() method is run at the very start when the game is initialized.
        public void Awake() {
            var pluginPath = System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var musicPath = pluginPath;            

            //attempt to parse settingsXml. Upon sucessful parse, default settings are changed and new music
            //is added to list musics. Upon failure, default settings kept and list musics is kept empty.
            try {
                var settingsXml = new XmlDocument();
                settingsXml.Load(pluginPath + "/settings.xml");
                settings = settingsXml["settings"];

                globalMusicVolume = float.Parse(settings["volume"].InnerText, CultureInfo.InvariantCulture);
                shouldLoop = settings["loop"].InnerText.ToLower() == "true";
                shouldPool = settings["pool"].InnerText.ToLower() == "true";

                if (settings["music-path"] != null) {
                    musicPath = settings["music-path"].InnerText;
                }
                soundFiles = SearchForAudioFiles(musicPath);

                foreach (XmlNode node in settings["music"].ChildNodes) {
                    if (node.NodeType != XmlNodeType.Comment) {
                        var newMusic = new Music();
                        newMusic.name = GetAttribute(node, "name");
                        newMusic.scenes = GetAttribute(node, "scenes").Split(',').Select(str => str.Trim()).ToArray();
                        newMusic.boss = GetAttribute(node, "boss").ToLower() == "true";
                        newMusic.volume = 1f;
                        var vol = GetAttribute(node, "volume");
                        if (vol != "") {
                            newMusic.volume = float.Parse(vol, CultureInfo.InvariantCulture);
                        }

                        foreach (var soundFile in soundFiles) {
                            if (soundFile.Name == newMusic.name) {
                                newMusic.fullName = soundFile.FullName;
                                break;
                            }
                        }

                        musics.Add(newMusic);
                    }
                }
            } catch (Exception ex) {
                Debug.LogWarning("!!!!! OriginalSoundTrack Mod: Failed to parse settings.xml !!!!!");
                Debug.Log("OriginalSoundTrack Mod: Music will be randomly selected from what is found in the plugin dir.");
                Debug.Log(ex);
            }

            if (soundFiles == null) {
                musicPath = pluginPath;
                soundFiles = SearchForAudioFiles(musicPath);
            }

            if (soundFiles.Length == 0) {
                Debug.LogError("!!!!! OriginalSoundTrack Mod: No audio files found. Exiting. !!!!!");
                Debug.LogError("OriginalSoundTrack Mod: Looked for .mp3 and .wav files in: " + musicPath);
                return;
            }

            if (outputDevice == null) {
                outputDevice = new WaveOutEvent();
            }

            On.RoR2.TeleporterInteraction.OnInteractionBegin += (orig, self, activator) => {
                orig(self, activator);
                if (!startedTeleporterEvent) {
                    startedTeleporterEvent = true;
                    PickOutMusic(true);
                }
            };

            On.RoR2.TeleporterInteraction.RpcClientOnActivated += (orig, self, activator) => {
                orig(self, activator);
                if (!startedTeleporterEvent) {
                    startedTeleporterEvent = true;
                    PickOutMusic(true);
                }
            };

            On.RoR2.UI.PauseScreenController.OnEnable += (orig, self) => {
                orig(self);
                if (outputDevice != null && outputDevice.PlaybackState == PlaybackState.Playing) {
                    outputDevice.Pause();
                    songPaused = true;
                }
            };

            On.RoR2.UI.PauseScreenController.OnDisable += (orig, self) => {
                orig(self);
                if (outputDevice != null && songPaused == true) {
                    outputDevice.Play();
                    songPaused = false;
                }
            };

            On.EntityStates.Missions.BrotherEncounter.Phase1.OnEnter += (orig, self) => {
                orig(self);
                Debug.Log("====================== FINAL BOSS FIGHT START ======================");
                PickOutMusic(true);
            };

            On.EntityStates.Missions.BrotherEncounter.EncounterFinished.OnEnter += (orig, self) => {
                orig(self);
                Debug.Log("====================== FINAL BOSS FIGHT DONE ======================");
            };

            SceneManager.sceneLoaded += (scene, mode) => {
                #if DEBUG
                    Debug.Log("====================== CHANGE SCENE ========================");
                    Debug.Log(scene.name);
                #endif

                if (currentScene != scene.name) {
                    currentScene = scene.name;
                    startedTeleporterEvent = false;
                    PickOutMusic();
                }
            };            

        }

        private FileInfo[] SearchForAudioFiles(string path) {
            var info = new DirectoryInfo(path);
            if (info.Exists) {
                return info.GetFiles()
                    .Where(f => System.IO.Path.GetExtension(f.Name) == ".mp3" || System.IO.Path.GetExtension(f.Name) == ".wav")
                    .ToArray();
            }
            FileInfo[] files = {};
            return files;
        }

        private string GetAttribute(XmlNode node, string attribute) {
            if (node.Attributes != null) {
                var attr = node.Attributes.GetNamedItem(attribute);
                if (attr != null) {
                    return attr.Value;
                }
            }
            return "";
        }

        private bool sceneMostlyMatches(string[] scenes) {
            // this handles scenes that can have numbers on the end.
            foreach (var scene in scenes) {
                if (currentScene.Contains(scene)) {
                    return true;
                }
            }
            return false;
        }

        private void PickOutMusic(bool isForTeleporter = false) {
            var goodMusicChoices = musics.Where(music => {
                var bossTest = isForTeleporter == music.boss;
                return music.fullName != null && bossTest && sceneMostlyMatches(music.scenes);
            }).ToArray();

            #if DEBUG
                Debug.Log("====== Selecting Music ======");
                Debug.Log("Current Scene: " + currentScene);
                Debug.Log("Is For Teleporter: " + isForTeleporter);
                Debug.Log("Choices: ");
                foreach(var choice in goodMusicChoices) {
                    Debug.Log(choice.name);
                }
                Debug.Log("");
            #endif

            var randFile = currentSongFullName;
            Music randMusic = null;
            int tries = 0;

            if (goodMusicChoices.Length > 0) {
                while (randFile == currentSongFullName && tries < 10) {
                    randMusic = goodMusicChoices[rnd.Next(goodMusicChoices.Length)];
                    randFile = randMusic.fullName;
                    tries++;
                }
                StartCoroutine(PlayMusic(randFile, randMusic.volume));
                return;
            }

            #if DEBUG
                Debug.Log("======= MUSIC PICK FAILED! =======");
                Debug.Log("choosing random song...");
            #endif
            // if we are here, then we failed to pick a music, so pick one at random from all of them we found.
            //if setting shouldPool is enabled, use the normal soundtrack from the game.
            if(shouldPool){
                //no new music is playing, so none is picked
                randFile = null;
            }else{
                tries = 0;
                while (randFile == currentSongFullName && tries < 10) {
                    randFile = soundFiles[rnd.Next(soundFiles.Length)].FullName;
                    tries++;
                }                
            }
            StartCoroutine(PlayMusic(randFile));            
        }

        private IEnumerator<WaitForSeconds> PlayMusic(string file, float volume = 1f) {            
            if (outputDevice.PlaybackState == PlaybackState.Playing) {
                fader.BeginFadeOut(1500);
                yield return new WaitForSeconds(1.5f);
                outputDevice.Stop();
                currentSong = null;
            }
            if(file == null){
                //no new music was selected, so return to OST pool
                //If already playing OST music from previous scene, 
                //then no need to unmute normal music again
                if(currentSongFullName!=null){
                    currentSongFullName = null;
                    unmuteNormalMusic();
                }                
                #if DEBUG
                    Debug.Log("PlayMusic: Playing music from OST");
                #endif
            }else{
                if (file != currentSongFullName) {
                    //new music provided, play that music.
                    //if we were already playing new music, 
                    //then don't mute normal volume again
                    if(currentSongFullName == null){
                        muteNormalMusic();
                    }
                    currentSongFullName = file;
                    currentSong = new AudioFileReader(file);
                    currentSong.Volume = volume * globalMusicVolume;
                    var looper = new LoopStream(currentSong, shouldLoop);
                    fader = new FadeInOutSampleProvider(new WaveToSampleProvider(looper));
                    outputDevice.Init(fader);
                    #if DEBUG
                        Debug.Log("====== Now Playing: " + file);
                    #endif
                    outputDevice.Play();
                    songPaused = false;
                } else {
                    #if DEBUG
                        Debug.Log("PlayMusic: Already playing: " + file);
                    #endif
                }
            }
        }

        public void Update() {     
            //obtain user-set music volume convar
            if (oldMusicVolume == "" && RoR2.Console.instance != null) {
                var convar = RoR2.Console.instance.FindConVar("volume_music");
                if (convar != null) {
                    oldMusicVolume = convar.GetString();
                }
            }
        }

        private void FixedUpdate() {
            if (currentSong != null) {
                if (currentSong.Position >= currentSong.Length && !shouldLoop) {
                    outputDevice.Stop();
                    currentSong.Position = currentSong.Length - 1;
                    PickOutMusic(startedTeleporterEvent);
                }
            }
        }

        //If the normal music is playing, then do not override this value on game exit
        private void OnDestroy() {
            var convar = RoR2.Console.instance.FindConVar("volume_music");
            if (convar != null && currentSongFullName != null) {
                convar.SetString(oldMusicVolume);
            }
        }

        private void muteNormalMusic(){
            if (RoR2.Console.instance != null) {
                var convar = RoR2.Console.instance.FindConVar("volume_music");
                // set in game music volume to 0 so we hear the new music only.
                if (convar != null) {
                    oldMusicVolume = convar.GetString();
                    convar.SetString("0");
                }
            }
        }

        private void unmuteNormalMusic(){
            if (RoR2.Console.instance != null) {
                var convar = RoR2.Console.instance.FindConVar("volume_music");
                // reset in game music volume so we hear the normal music.
                if (convar != null && !oldMusicVolume.Equals(String.Empty)) {
                    convar.SetString(oldMusicVolume);
                }
            }
        }
    }

    public class Music {
        public string name;
        public string fullName;
        public string[] scenes;
        public bool boss = false;
        public float volume = 1f;
    }

    public class LoopStream : WaveStream {

        WaveStream sourceStream;
        bool EnableLooping = true;

        public LoopStream(WaveStream sourceStream, bool shouldLoop) {
            this.sourceStream = sourceStream;
            this.EnableLooping = shouldLoop;
        }

        public override WaveFormat WaveFormat {
            get { return sourceStream.WaveFormat; }
        }

        public override long Position {
            get { return sourceStream.Position; }
            set { sourceStream.Position = value; }
        }

        public override long Length {
            get { return sourceStream.Length; }
        }

        public override int Read(byte[] buffer, int offset, int count) {
            int read = 0;
            while (read < count) {
                int required = count - read;
                int readThisTime = sourceStream.Read(buffer, offset + read, required);
                if (readThisTime < required || sourceStream.Position >= sourceStream.Length) {
                    if (!EnableLooping) {
                        break;
                    }
                    sourceStream.Position = 0;
                }
                read += readThisTime;
            }
            return read;
        }
    }
}
