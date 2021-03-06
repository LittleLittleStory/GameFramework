﻿//------------------------------------------------------------
// Game Framework
// Copyright © 2013-2020 Jiang Yin. All rights reserved.
// Homepage: https://gameframework.cn/
// Feedback: mailto:ellan@gameframework.cn
//------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;

namespace GameFramework.Resource
{
    internal sealed partial class ResourceManager : GameFrameworkModule, IResourceManager
    {
        /// <summary>
        /// 资源检查器。
        /// </summary>
        private sealed partial class ResourceChecker
        {
            private readonly ResourceManager m_ResourceManager;
            private readonly Dictionary<ResourceName, CheckInfo> m_CheckInfos;
            private string m_CurrentVariant;
            private bool m_UpdatableVersionListReady;
            private bool m_ReadOnlyVersionListReady;
            private bool m_ReadWriteVersionListReady;

            public GameFrameworkAction<ResourceName, LoadType, int, int, int, int> ResourceNeedUpdate;
            public GameFrameworkAction<int, int, long, long> ResourceCheckComplete;

            /// <summary>
            /// 初始化资源检查器的新实例。
            /// </summary>
            /// <param name="resourceManager">资源管理器。</param>
            public ResourceChecker(ResourceManager resourceManager)
            {
                m_ResourceManager = resourceManager;
                m_CheckInfos = new Dictionary<ResourceName, CheckInfo>();
                m_CurrentVariant = null;
                m_UpdatableVersionListReady = false;
                m_ReadOnlyVersionListReady = false;
                m_ReadWriteVersionListReady = false;

                ResourceNeedUpdate = null;
                ResourceCheckComplete = null;
            }

            /// <summary>
            /// 关闭并清理资源检查器。
            /// </summary>
            public void Shutdown()
            {
                m_CheckInfos.Clear();
            }

            public void CheckResources(string currentVariant)
            {
                m_CurrentVariant = currentVariant;

                TryRecoverReadWriteVersionList();

                if (m_ResourceManager.m_ResourceHelper == null)
                {
                    throw new GameFrameworkException("Resource helper is invalid.");
                }

                if (string.IsNullOrEmpty(m_ResourceManager.m_ReadOnlyPath))
                {
                    throw new GameFrameworkException("Readonly path is invalid.");
                }

                if (string.IsNullOrEmpty(m_ResourceManager.m_ReadWritePath))
                {
                    throw new GameFrameworkException("Read-write path is invalid.");
                }

                m_ResourceManager.m_ResourceHelper.LoadBytes(Utility.Path.GetRemotePath(Path.Combine(m_ResourceManager.m_ReadWritePath, Utility.Path.GetResourceNameWithSuffix(VersionListFileName))), ParseUpdatableVersionList);
                m_ResourceManager.m_ResourceHelper.LoadBytes(Utility.Path.GetRemotePath(Path.Combine(m_ResourceManager.m_ReadOnlyPath, Utility.Path.GetResourceNameWithSuffix(ResourceListFileName))), ParseReadOnlyVersionList);
                m_ResourceManager.m_ResourceHelper.LoadBytes(Utility.Path.GetRemotePath(Path.Combine(m_ResourceManager.m_ReadWritePath, Utility.Path.GetResourceNameWithSuffix(ResourceListFileName))), ParseReadWriteVersionList);
            }

            private void SetVersionInfo(ResourceName resourceName, LoadType loadType, int length, int hashCode, int zipLength, int zipHashCode)
            {
                GetOrAddCheckInfo(resourceName).SetVersionInfo(loadType, length, hashCode, zipLength, zipHashCode);
            }

            private void SetReadOnlyInfo(ResourceName resourceName, LoadType loadType, int length, int hashCode)
            {
                GetOrAddCheckInfo(resourceName).SetReadOnlyInfo(loadType, length, hashCode);
            }

            private void SetReadWriteInfo(ResourceName resourceName, LoadType loadType, int length, int hashCode)
            {
                GetOrAddCheckInfo(resourceName).SetReadWriteInfo(loadType, length, hashCode);
            }

            private CheckInfo GetOrAddCheckInfo(ResourceName resourceName)
            {
                CheckInfo checkInfo = null;
                if (m_CheckInfos.TryGetValue(resourceName, out checkInfo))
                {
                    return checkInfo;
                }

                checkInfo = new CheckInfo(resourceName);
                m_CheckInfos.Add(checkInfo.ResourceName, checkInfo);

                return checkInfo;
            }

            private void RefreshCheckInfoStatus()
            {
                if (!m_UpdatableVersionListReady || !m_ReadOnlyVersionListReady || !m_ReadWriteVersionListReady)
                {
                    return;
                }

                int removedCount = 0;
                int updateCount = 0;
                long updateTotalLength = 0L;
                long updateTotalZipLength = 0L;
                foreach (KeyValuePair<ResourceName, CheckInfo> checkInfo in m_CheckInfos)
                {
                    CheckInfo ci = checkInfo.Value;
                    ci.RefreshStatus(m_CurrentVariant);

                    if (ci.Status == CheckInfo.CheckStatus.StorageInReadOnly)
                    {
                        m_ResourceManager.m_ResourceInfos.Add(ci.ResourceName, new ResourceInfo(ci.ResourceName, ci.LoadType, ci.Length, ci.HashCode, true));
                    }
                    else if (ci.Status == CheckInfo.CheckStatus.StorageInReadWrite)
                    {
                        m_ResourceManager.m_ResourceInfos.Add(ci.ResourceName, new ResourceInfo(ci.ResourceName, ci.LoadType, ci.Length, ci.HashCode, false));
                    }
                    else if (ci.Status == CheckInfo.CheckStatus.NeedUpdate)
                    {
                        updateCount++;
                        updateTotalLength += ci.Length;
                        updateTotalZipLength += ci.ZipLength;

                        ResourceNeedUpdate(ci.ResourceName, ci.LoadType, ci.Length, ci.HashCode, ci.ZipLength, ci.ZipHashCode);
                    }
                    else if (ci.Status == CheckInfo.CheckStatus.Disuse || ci.Status == CheckInfo.CheckStatus.Unavailable)
                    {
                        // Do nothing.
                    }
                    else
                    {
                        throw new GameFrameworkException(Utility.Text.Format("Check resources '{0}' error with unknown status.", ci.ResourceName.FullName));
                    }

                    if (ci.NeedRemove)
                    {
                        removedCount++;
                        File.Delete(Utility.Path.GetRegularPath(Path.Combine(m_ResourceManager.m_ReadWritePath, Utility.Path.GetResourceNameWithSuffix(ci.ResourceName.FullName))));
                        m_ResourceManager.m_ReadWriteResourceInfos.Remove(ci.ResourceName);
                    }
                }

                if (removedCount > 0)
                {
                    Utility.Path.RemoveEmptyDirectory(m_ResourceManager.m_ReadWritePath);
                }

                ResourceCheckComplete(removedCount, updateCount, updateTotalLength, updateTotalZipLength);
            }

            /// <summary>
            /// 尝试恢复读写区版本资源列表。
            /// </summary>
            /// <returns>是否恢复成功。</returns>
            private bool TryRecoverReadWriteVersionList()
            {
                string file = Utility.Path.GetRegularPath(Path.Combine(m_ResourceManager.m_ReadWritePath, Utility.Path.GetResourceNameWithSuffix(ResourceListFileName)));
                string backupFile = file + BackupFileSuffixName;

                try
                {
                    if (!File.Exists(backupFile))
                    {
                        return false;
                    }

                    if (File.Exists(file))
                    {
                        File.Delete(file);
                    }

                    File.Move(backupFile, file);
                }
                catch
                {
                    return false;
                }

                return true;
            }

            /// <summary>
            /// 解析版本资源列表。
            /// </summary>
            /// <param name="fileUri">版本资源列表文件路径。</param>
            /// <param name="bytes">要解析的数据。</param>
            /// <param name="errorMessage">错误信息。</param>
            private void ParseUpdatableVersionList(string fileUri, byte[] bytes, string errorMessage)
            {
                if (m_UpdatableVersionListReady)
                {
                    throw new GameFrameworkException("Updatable version list has been parsed.");
                }

                if (bytes == null || bytes.Length <= 0)
                {
                    throw new GameFrameworkException(Utility.Text.Format("Updatable version list '{0}' is invalid, error message is '{1}'.", fileUri, string.IsNullOrEmpty(errorMessage) ? "<Empty>" : errorMessage));
                }

                MemoryStream memoryStream = null;
                try
                {
                    memoryStream = new MemoryStream(bytes, false);
                    UpdatableVersionList versionList = m_ResourceManager.m_UpdatableVersionListSerializer.Deserialize(memoryStream);
                    if (!versionList.IsValid)
                    {
                        throw new GameFrameworkException("Deserialize updatable version list failure.");
                    }

                    UpdatableVersionList.Asset[] assets = versionList.GetAssets();
                    UpdatableVersionList.Resource[] resources = versionList.GetResources();
                    UpdatableVersionList.ResourceGroup[] resourceGroups = versionList.GetResourceGroups();
                    m_ResourceManager.m_ApplicableGameVersion = versionList.ApplicableGameVersion;
                    m_ResourceManager.m_InternalResourceVersion = versionList.InternalResourceVersion;
                    m_ResourceManager.m_AssetInfos = new Dictionary<string, AssetInfo>(assets.Length);
                    m_ResourceManager.m_ResourceInfos = new Dictionary<ResourceName, ResourceInfo>(resources.Length, new ResourceNameComparer());
                    ResourceGroup defaultResourceGroup = m_ResourceManager.GetOrAddResourceGroup(string.Empty);

                    foreach (UpdatableVersionList.Resource resource in resources)
                    {
                        ResourceName resourceName = new ResourceName(resource.Name, resource.Variant);
                        int[] assetIndexes = resource.GetAssetIndexes();
                        foreach (int assetIndex in assetIndexes)
                        {
                            UpdatableVersionList.Asset asset = assets[assetIndex];
                            int[] dependencyAssetIndexes = asset.GetDependencyAssetIndexes();
                            string[] dependencyAssetNames = new string[dependencyAssetIndexes.Length];
                            int index = 0;
                            foreach (int dependencyAssetIndex in dependencyAssetIndexes)
                            {
                                dependencyAssetNames[index++] = assets[dependencyAssetIndex].Name;
                            }

                            if (resource.Variant == null || resource.Variant == m_CurrentVariant)
                            {
                                m_ResourceManager.m_AssetInfos.Add(asset.Name, new AssetInfo(asset.Name, resourceName, dependencyAssetNames));
                            }
                        }

                        if (resource.Variant == null || resource.Variant == m_CurrentVariant)
                        {
                            SetVersionInfo(resourceName, (LoadType)resource.LoadType, resource.Length, resource.HashCode, resource.ZipLength, resource.ZipHashCode);
                            defaultResourceGroup.AddResource(resourceName, resource.Length, resource.ZipLength);
                        }
                    }

                    foreach (UpdatableVersionList.ResourceGroup resourceGroup in resourceGroups)
                    {
                        ResourceGroup group = m_ResourceManager.GetOrAddResourceGroup(resourceGroup.Name);
                        int[] resourceIndexes = resourceGroup.GetResourceIndexes();
                        foreach (int resourceIndex in resourceIndexes)
                        {
                            if (resources[resourceIndex].Variant == null || resources[resourceIndex].Variant == m_CurrentVariant)
                            {
                                group.AddResource(new ResourceName(resources[resourceIndex].Name, resources[resourceIndex].Variant), resources[resourceIndex].Length, resources[resourceIndex].ZipLength);
                            }
                        }
                    }

                    m_UpdatableVersionListReady = true;
                    RefreshCheckInfoStatus();
                }
                catch (Exception exception)
                {
                    if (exception is GameFrameworkException)
                    {
                        throw;
                    }

                    throw new GameFrameworkException(Utility.Text.Format("Parse updatable version list exception '{0}'.", exception.ToString()), exception);
                }
                finally
                {
                    if (memoryStream != null)
                    {
                        memoryStream.Dispose();
                        memoryStream = null;
                    }
                }
            }

            /// <summary>
            /// 解析只读区版本资源列表。
            /// </summary>
            /// <param name="fileUri">只读区版本资源列表文件路径。</param>
            /// <param name="bytes">要解析的数据。</param>
            /// <param name="errorMessage">错误信息。</param>
            private void ParseReadOnlyVersionList(string fileUri, byte[] bytes, string errorMessage)
            {
                if (m_ReadOnlyVersionListReady)
                {
                    throw new GameFrameworkException("Read only version list has been parsed.");
                }

                if (bytes == null || bytes.Length <= 0)
                {
                    m_ReadOnlyVersionListReady = true;
                    RefreshCheckInfoStatus();
                    return;
                }

                MemoryStream memoryStream = null;
                try
                {
                    memoryStream = new MemoryStream(bytes, false);
                    LocalVersionList versionList = m_ResourceManager.m_ReadOnlyVersionListSerializer.Deserialize(memoryStream);
                    if (!versionList.IsValid)
                    {
                        throw new GameFrameworkException("Deserialize read only version list failure.");
                    }

                    LocalVersionList.Resource[] resources = versionList.GetResources();
                    foreach (LocalVersionList.Resource resource in resources)
                    {
                        SetReadOnlyInfo(new ResourceName(resource.Name, resource.Variant), (LoadType)resource.LoadType, resource.Length, resource.HashCode);
                    }

                    m_ReadOnlyVersionListReady = true;
                    RefreshCheckInfoStatus();
                }
                catch (Exception exception)
                {
                    if (exception is GameFrameworkException)
                    {
                        throw;
                    }

                    throw new GameFrameworkException(Utility.Text.Format("Parse read only version list exception '{0}'.", exception.ToString()), exception);
                }
                finally
                {
                    if (memoryStream != null)
                    {
                        memoryStream.Dispose();
                        memoryStream = null;
                    }
                }
            }

            /// <summary>
            /// 解析读写区版本资源列表。
            /// </summary>
            /// <param name="fileUri">读写区版本资源列表文件路径。</param>
            /// <param name="bytes">要解析的数据。</param>
            /// <param name="errorMessage">错误信息。</param>
            private void ParseReadWriteVersionList(string fileUri, byte[] bytes, string errorMessage)
            {
                if (m_ReadWriteVersionListReady)
                {
                    throw new GameFrameworkException("Read write version list has been parsed.");
                }

                if (bytes == null || bytes.Length <= 0)
                {
                    m_ReadWriteVersionListReady = true;
                    RefreshCheckInfoStatus();
                    return;
                }

                MemoryStream memoryStream = null;
                try
                {
                    memoryStream = new MemoryStream(bytes, false);
                    LocalVersionList versionList = m_ResourceManager.m_ReadWriteVersionListSerializer.Deserialize(memoryStream);
                    if (!versionList.IsValid)
                    {
                        throw new GameFrameworkException("Deserialize read write version list failure.");
                    }

                    LocalVersionList.Resource[] resources = versionList.GetResources();
                    foreach (LocalVersionList.Resource resource in resources)
                    {
                        ResourceName resourceName = new ResourceName(resource.Name, resource.Variant);
                        SetReadWriteInfo(resourceName, (LoadType)resource.LoadType, resource.Length, resource.HashCode);
                        m_ResourceManager.m_ReadWriteResourceInfos.Add(resourceName, new ReadWriteResourceInfo((LoadType)resource.LoadType, resource.Length, resource.HashCode));
                    }

                    m_ReadWriteVersionListReady = true;
                    RefreshCheckInfoStatus();
                }
                catch (Exception exception)
                {
                    if (exception is GameFrameworkException)
                    {
                        throw;
                    }

                    throw new GameFrameworkException(Utility.Text.Format("Parse read write version list exception '{0}'.", exception.ToString()), exception);
                }
                finally
                {
                    if (memoryStream != null)
                    {
                        memoryStream.Dispose();
                        memoryStream = null;
                    }
                }
            }
        }
    }
}
