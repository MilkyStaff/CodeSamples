using Sirenix.OdinInspector;
using Sirenix.Serialization;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Pool;
using TowerId = AllEnums.TowerId;
using CritterType = AllEnums.CritterType;
using static AllEnums;
using Random = UnityEngine.Random;
using Unity.Mathematics;

public class SimpleEffectManager : SerializedScriptableObject
{
    private const int defaultPoolCount = 10;
    private const int defaultPoolCapasity = 10;
    private const int maxPoolCapasity = 50;
    private GameObject effectsRoot, towersRoot, projectilesRoot, crittersRoot, environmentRoot;

    [OdinSerialize] public Dictionary<TowerId, GameObject> TowerPrefabs;
    [NonSerialized] public Dictionary<TowerId, IObjectPool<GameObject>> TowersDict = new();

    [OdinSerialize, BoxGroup("Projectiles")]
    public Dictionary<TowerId, GameObject> ProjectilePrefabs;
    [NonSerialized] public Dictionary<TowerId, IObjectPool<GameObject>> ProjectilesDict = new();

    [OdinSerialize, BoxGroup("Projectiles")]
    public Dictionary<TowerId, GameObject> EnhancedProjectilePrefabs;
    [NonSerialized] public Dictionary<TowerId, IObjectPool<GameObject>> EnhancedProjectilesDict = new();

    [OdinSerialize, BoxGroup("TagEffects")]
    public Dictionary<TagEffectType, GameObject> TagEffectPrefabs;
    [NonSerialized] public Dictionary<TagEffectType, IObjectPool<GameObject>> TagEffectsDict = new();


    [SerializeField] private List<EnvironmentVisual> environmentVisuals = new();
    [NonSerialized] private List<GameObject> staticEnvironments = new();

    [OdinSerialize] public Dictionary<CritterType, GameObject> CritterPrefabs;
    [NonSerialized] public Dictionary<CritterType, IObjectPool<GameObject>> CrittersDict = new();

    [SerializeField, BoxGroup("Blood")] public float PercentForBlood = 0.34f;
    [OdinSerialize, BoxGroup("Blood")] public Dictionary<ArmorType, List<Texture2D>> BioBloodDecalPrefabs;
    [OdinSerialize, BoxGroup("Blood")] public Dictionary<FleshType, List<Texture2D>> ArtificialBloodDecalPrefabs;
    [OdinSerialize, BoxGroup("Blood")] public Dictionary<ArmorType, List<Texture2D>> BioDeathDecalPrefabs;
    [OdinSerialize, BoxGroup("Blood")] public Dictionary<FleshType, List<Texture2D>> ArtificialDeathDecalPrefabs;

    public bool TryGetBloodDecalTexture(FleshType creepBloodType, ArmorType armorType, out Texture2D result)
    {
        bool check = creepBloodType is FleshType.Bio ?
            BioBloodDecalPrefabs.TryGetValue(armorType, out List<Texture2D> resultsList) :
            ArtificialBloodDecalPrefabs.TryGetValue(creepBloodType, out resultsList);
        result = resultsList?[Random.Range(0, resultsList.Count)];
        return check;
    }

    public bool TryGetDeathDecalTexture(FleshType creepBloodType, ArmorType armorType, out Texture2D result)
    {
        bool check = creepBloodType is FleshType.Bio ?
            BioDeathDecalPrefabs.TryGetValue(armorType, out List<Texture2D> resultsList) :
            ArtificialDeathDecalPrefabs.TryGetValue(creepBloodType, out resultsList);
        result = resultsList?[Random.Range(0, resultsList.Count)];
        return check;
    }

    public void Init()
    {
        CreateRoots();

        CreateDictionaryPool<TowerId>();
        CreateDictionaryPool<CritterType>();
        CreateDictionaryPool<TagEffectType>();
    }

    public void ClearStaticEnvironments()
    {
        foreach (GameObject environment in staticEnvironments)
        {
            if (environment != null)
                Destroy(environment);
        }

        staticEnvironments.Clear();
    }

    public void ClearPools()
    {
        TowersDict.Clear();
        ProjectilesDict.Clear();
        EnhancedProjectilesDict.Clear();
        TagEffectsDict.Clear();

        CrittersDict.Clear();

        ClearStaticEnvironments();
    }

    /// <summary>which will be released after restart</summary>
    public T GetSimpleVisual<T>() where T : EnvironmentVisual
    {
        foreach (EnvironmentVisual environment in environmentVisuals)
        {
            if (environment.name == typeof(T).ToString())
            {
                T go = Instantiate(environment, environmentRoot.transform) as T;
                if (go != null)
                {
                    staticEnvironments.Add(go.gameObject);
                    return go;
                }
            }
        }

        return null;
    }

    public void CreateRoots()
    {
        effectsRoot = FindOrCreate(nameof(effectsRoot));

        towersRoot = FindOrCreate(nameof(towersRoot));
        projectilesRoot = FindOrCreate(nameof(projectilesRoot));
        crittersRoot = FindOrCreate(nameof(crittersRoot));
        environmentRoot = FindOrCreate(nameof(environmentRoot));

        towersRoot.transform.parent =
            projectilesRoot.transform.parent =
                crittersRoot.transform.parent =
                    environmentRoot.transform.parent = effectsRoot.transform;

        GameObject FindOrCreate(string name)
        {
            var result = GameObject.Find(name);
            if (result == null) { result = new GameObject(name); }


            return result;
        }
    }

    private void CreateDictionaryPool<T>() where T : struct, Enum
    {
        foreach (T enumID in Enum.GetValues(typeof(T)))
        {
            switch (enumID)
            {
                case TowerId towerId:
                    FillDict(TowersDict, TowerPrefabs, towerId, defaultPoolCount, towersRoot.transform);
                    FillDict(ProjectilesDict, ProjectilePrefabs, towerId, defaultPoolCount, projectilesRoot.transform);
                    FillDict(EnhancedProjectilesDict, EnhancedProjectilePrefabs, towerId, defaultPoolCount, projectilesRoot.transform);
                    break;
                case CritterType critterType:
                    FillDict(CrittersDict, CritterPrefabs, critterType, defaultPoolCount, crittersRoot.transform);
                    break;
                case TagEffectType effectType:
                    FillDict(TagEffectsDict, TagEffectPrefabs, effectType, defaultPoolCount, effectsRoot.transform);
                    break;
            }
        }
    }

    private void FillDict<T>(Dictionary<T, IObjectPool<GameObject>> dict, Dictionary<T, GameObject> prefabs, T enumType, int count, Transform root)
    {
        try
        {
            IObjectPool<GameObject> tempPool = new ObjectPool<GameObject>(() => CreatePooledItem(enumType, prefabs, root), OnTakeFromPool, OnReturnedToPool, OnDestroyPoolObject, true,
                defaultPoolCapasity,
                maxPoolCapasity);

            dict.Add(enumType, tempPool);

            for (int i = 0; i < count; i++)
                tempPool.Release(CreatePooledItem(enumType, prefabs, root));
        }
        catch
        {
            throw new Exception($" enumType {enumType} is added");
        }
    }

    private void OnDestroyPoolObject(GameObject obj)
    {
        Destroy(obj);
    }

    private void OnReturnedToPool(GameObject obj)
    {
        if (obj == null)
            return;

        obj.SetActive(false);
    }

    private void OnTakeFromPool(GameObject obj)
    {
        obj.SetActive(true);
    }

    private GameObject CreatePooledItem<T>(T prefabKey, Dictionary<T, GameObject> prefabs, Transform root)
    {
        prefabs.TryGetValue(prefabKey, out GameObject go);
        return Instantiate(go, root);
    }
}