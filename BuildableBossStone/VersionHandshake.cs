﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;

namespace BuildableBossStone
{
    [HarmonyPatch(typeof(ZNet), nameof(ZNet.OnNewConnection))]
    public static class RegisterAndCheckVersion
    {
        private static void Prefix(ZNetPeer peer, ref ZNet __instance)
        {
            // Register version check call
            BuildableBossStonePlugin.BuildableBossStoneLogger.LogDebug("Registering version RPC handler");
            peer.m_rpc.Register($"{BuildableBossStonePlugin.ModName}_VersionCheck",
                new Action<ZRpc, ZPackage>(RpcHandlers.RPC_BuildableBossStone_Version));

            // Make calls to check versions
            BuildableBossStonePlugin.BuildableBossStoneLogger.LogInfo("Invoking version check");
            ZPackage zpackage = new();
            zpackage.Write(BuildableBossStonePlugin.ModVersion);
            zpackage.Write(RpcHandlers.ComputeHashForMod().Replace("-", ""));
            peer.m_rpc.Invoke($"{BuildableBossStonePlugin.ModName}_VersionCheck", zpackage);
        }
    }

    [HarmonyPatch(typeof(ZNet), nameof(ZNet.RPC_PeerInfo))]
    public static class VerifyClient
    {
        private static bool Prefix(ZRpc rpc, ZPackage pkg, ref ZNet __instance)
        {
            if (!__instance.IsServer() || RpcHandlers.ValidatedPeers.Contains(rpc)) return true;
            // Disconnect peer if they didn't send mod version at all
            BuildableBossStonePlugin.BuildableBossStoneLogger.LogWarning(
                $"Peer ({rpc.m_socket.GetHostName()}) never sent version or couldn't due to previous disconnect, disconnecting");
            rpc.Invoke("Error", 3);
            return false; // Prevent calling underlying method
        }

        private static void Postfix(ZNet __instance)
        {
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(),
                $"{BuildableBossStonePlugin.ModName}RequestAdminSync",
                new ZPackage());
        }
    }

    [HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.ShowConnectError))]
    public class ShowConnectionError
    {
        private static void Postfix(FejdStartup __instance)
        {
            if (__instance.m_connectionFailedPanel.activeSelf)
            {
                __instance.m_connectionFailedError.fontSizeMax = 25;
                __instance.m_connectionFailedError.fontSizeMax = 15;
                __instance.m_connectionFailedError.text += "\n" + BuildableBossStonePlugin.ConnectionError;
            }
        }
    }

    [HarmonyPatch(typeof(ZNet), nameof(ZNet.Disconnect))]
    public static class RemoveDisconnectedPeerFromVerified
    {
        private static void Prefix(ZNetPeer peer, ref ZNet __instance)
        {
            if (!__instance.IsServer()) return;
            // Remove peer from validated list
            BuildableBossStonePlugin.BuildableBossStoneLogger.LogInfo(
                $"Peer ({peer.m_rpc.m_socket.GetHostName()}) disconnected, removing from validated list");
            _ = RpcHandlers.ValidatedPeers.Remove(peer.m_rpc);
        }
    }

    public static class RpcHandlers
    {
        public static readonly List<ZRpc> ValidatedPeers = new();

        public static void RPC_BuildableBossStone_Version(ZRpc rpc, ZPackage pkg)
        {
            string? version = pkg.ReadString();
            string? hash = pkg.ReadString();

            var hashForAssembly = ComputeHashForMod().Replace("-", "");
            BuildableBossStonePlugin.BuildableBossStoneLogger.LogInfo("Version check, local: " +
                                                                      BuildableBossStonePlugin.ModVersion +
                                                                      ",  remote: " + version);
            if (hash != hashForAssembly || version != BuildableBossStonePlugin.ModVersion)
            {
                BuildableBossStonePlugin.ConnectionError =
                    $"{BuildableBossStonePlugin.ModName} Installed: {BuildableBossStonePlugin.ModVersion} {hashForAssembly}\n Needed: {version} {hash}";
                if (!ZNet.instance.IsServer()) return;
                // Different versions - force disconnect client from server
                BuildableBossStonePlugin.BuildableBossStoneLogger.LogWarning(
                    $"Peer ({rpc.m_socket.GetHostName()}) has incompatible version, disconnecting...");
                rpc.Invoke("Error", 3);
            }
            else
            {
                if (!ZNet.instance.IsServer())
                {
                    // Enable mod on client if versions match
                    BuildableBossStonePlugin.BuildableBossStoneLogger.LogInfo("Received same version from server!");
                }
                else
                {
                    // Add client to validated list
                    BuildableBossStonePlugin.BuildableBossStoneLogger.LogInfo(
                        $"Adding peer ({rpc.m_socket.GetHostName()}) to validated list");
                    ValidatedPeers.Add(rpc);
                }
            }
        }

        public static string ComputeHashForMod()
        {
            using SHA256 sha256Hash = SHA256.Create();
            // ComputeHash - returns byte array  
            byte[] bytes = sha256Hash.ComputeHash(File.ReadAllBytes(Assembly.GetExecutingAssembly().Location));
            // Convert byte array to a string   
            StringBuilder builder = new();
            foreach (byte b in bytes)
            {
                builder.Append(b.ToString("X2"));
            }

            return builder.ToString();
        }
    }
}