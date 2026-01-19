using OBSWebsocketDotNet;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace SysTrayOBS
{
    public class OBSProgram
    {
        private CancellationTokenSource keepAliveTokenSource;
        OBSWebsocket obs = new OBSWebsocket();
        delegate void ActionToPerform();
        ActionToPerform ActionToPerformAfterConnect = null;

        public OBSProgram()
        {
            obs.Connected += onConnect;
            obs.Disconnected += onDisconnect;
        }

        public void Connect()
        {
            if (!obs.IsConnected)
            {
                try
                {
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        obs.ConnectAsync("ws://192.168.1.175:4455", "nlqvRFeoy3Z9V1xL");
                    });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"OBS connection failed: {ex.Message}");
                }
            }
        }

        public void Disconnect()
        {
            if (obs.IsConnected)
            {
                obs.Disconnect();
                Console.WriteLine("Disconnected");
            }
        }
        private void onConnect(object sender, EventArgs e)
        {
            Debug.WriteLine("on connect");
            keepAliveTokenSource = new CancellationTokenSource();
            CancellationToken keepAliveToken = keepAliveTokenSource.Token;
            ActionToPerformAfterConnect?.Invoke();            
            Disconnect();
            ActionToPerformAfterConnect = null;
        }
        private void onDisconnect(object sender, OBSWebsocketDotNet.Communication.ObsDisconnectionInfo e)
        {
            Debug.WriteLine("OBS Disconnected signal..");
        }

        public void ToggleSetSceneCamAndMic()
        {
            if(ActionToPerformAfterConnect != null)
            {
                // An action is already queued, do not queue another
                return;
            }
            ActionToPerformAfterConnect = delToggleSetSceneCamAndMic;
            Connect();
        }
        public void ToggleScene()
        {
            if (ActionToPerformAfterConnect != null)
            {
                // An action is already queued, do not queue another
                return;
            }
            ActionToPerformAfterConnect = delToggleScene;
            Connect();
        }

        public void ToggleStream()
        {
            if (ActionToPerformAfterConnect != null)
            {
                // An action is already queued, do not queue another
                return;
            }
            ActionToPerformAfterConnect = delToggleStream;
            Connect();
        }

        void delToggleScene()
        {
            string currentScene = obs.GetCurrentProgramScene();
            if(currentScene.Equals("scene", StringComparison.OrdinalIgnoreCase))
            {
                obs.SetCurrentProgramScene("BRB");
            }
            else
            {
                obs.SetCurrentProgramScene("Scene");
            }                        
        }

        void delToggleStream()
        {
            var isStreaming = obs.GetStreamStatus().IsActive;  
            if(isStreaming)
            {
                obs.StopStream();
            }
            else
            {
                obs.StartStream();
            }
        }

        void delToggleSetSceneCamAndMic()
        {
            var sceneName = "Scene";
            var itemNameMIC = "MIC";
            var itemNameWebCam = "WEBCAM";            
            var sceneItems = obs.GetSceneItemList(sceneName);
            var micItem = sceneItems.FirstOrDefault(i => i.SourceName == itemNameMIC);
            var webcamItem = sceneItems.FirstOrDefault(i => i.SourceName == itemNameWebCam);

            if (micItem == null)
                throw new Exception("MIC scene item not found");
            if (webcamItem == null)
                throw new Exception("WEBCAM scene item not found");

            var micItemEnabled = obs.GetSceneItemEnabled(sceneName, micItem.ItemId);
            var webcamItemEnabled = obs.GetSceneItemEnabled(sceneName, micItem.ItemId);

            var toggle = !(micItemEnabled == true || webcamItemEnabled == true);

            obs.SetSceneItemEnabled(
                sceneName,
                micItem.ItemId,
                toggle
            );
            obs.SetSceneItemEnabled(
                sceneName,
                webcamItem.ItemId,
                toggle
            );
        }
    }
}
