using UnityEngine;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Jobs;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Burst;
using Unity.Entities;
using ECSTest.Components;
using ECSTest.Systems;
using NativeTrees;
using Unity.Transforms;
using Unity.Jobs.LowLevel.Unsafe;

[BurstCompile]
[UpdateInGroup(typeof(BeforeFixedSimulationGroup))]
public partial class TileDecalSystem : SystemBase
{
    private const float pi = Mathf.PI;//3.14159f;
    public const int PixelsPerUnit = 50;

    private const float gaussClearRange = 0.3f;
    private const float gaussFullClearMult = 0.4f;

    private const string environmentLayerName = "Environment";

    private NativeArray<Color32> tileDecalsPixels;
    private NativeArray<bool> isFloorPositions;

    private int mapSizeX;
    private int mapSizeY;
    private const int mapZeroPoint = 0;

    private SpriteRenderer oneRenderer;

    private float tempCameraSize;
    private Vector3 tempCameraPosition;
    private Camera rendererCamera;

    /// <summary>
    /// for DynamicTexture
    private Texture2D tempTexture;
    private float2 dynamicTextureOversize = new float2(.1f, .1f);
    private int2 lowerPixel;
    private int2 upperPixel;

    private void CreateDynamicTexture()
    {
        tempCameraSize = rendererCamera.orthographicSize;
        UpdateDynamicRendererBorders();

        int width = upperPixel.x - lowerPixel.x;
        int height = upperPixel.y - lowerPixel.y;
        tempTexture = new Texture2D(width, height, TextureFormat.RGBA32, false);

        Sprite tempSprite = Sprite.Create(tempTexture, new Rect(0.0f, 0.0f, width, height), new Vector2(.5f, .5f), PixelsPerUnit);
        oneRenderer.sprite = tempSprite;
        //RenderVisibleDecals();
    }
    /// </summary>

    public void Init(Cell[,] cells)
    {
        var rendererGo = new GameObject("DecalsRoot");
        oneRenderer = rendererGo.AddComponent<SpriteRenderer>();
        oneRenderer.sortingLayerName = environmentLayerName;
        oneRenderer.sortingOrder = -1;
        rendererCamera = Camera.main;
        tempCameraSize = rendererCamera.orthographicSize;

        mapSizeX = cells.GetLength(0) + 1;
        mapSizeY = cells.GetLength(1) + 1;

        tileDecalsPixels = new NativeArray<Color32>(mapSizeX * mapSizeY * PixelsPerUnit * PixelsPerUnit, Allocator.Persistent);
        oneRenderer.sprite = null;

        isFloorPositions = new NativeArray<bool>(mapSizeX * mapSizeY, Allocator.Persistent);
        for (int y = 0; y < cells.GetLength(1); y++)
            for (int x = 0; x < cells.GetLength(0); x++)
                isFloorPositions[x + y * mapSizeX] = !cells[x, y].IsWall;

        CreateDynamicTexture();
    }

    protected override void OnDestroy()
    {
        Dispose();
        base.OnDestroy();
    }

    public void Dispose()
    {
        if (tileDecalsPixels.IsCreated)
            tileDecalsPixels.Dispose();
        if (isFloorPositions.IsCreated)
            isFloorPositions.Dispose();
    }

    private JobHandle renderTextureJobHandle;

    protected unsafe override void OnUpdate()
    {
        if (rendererCamera == null || tempTexture == null || tempTexture.width == 0 || tempTexture.height == 0)
            return;

        foreach (var (critter, position) in SystemAPI.Query<CritterComponent, PositionComponent>())
            CleanTileDecalInRange(position.Position, critter.Radius, critter.CleaningQuality);

        if (rendererCamera.transform.position != tempCameraPosition)
            UpdateDynamicRendererBorders();

        renderTextureJobHandle = RenderVisibleDecals();
    }

    public void CompleteRender()
    {
        if (!renderTextureJobHandle.IsCompleted)
            renderTextureJobHandle.Complete();
        tempTexture.Apply(false);
    }

    public void ClearTileDecals()
    {
        if (tileDecalsPixels.IsCreated)
            tileDecalsPixels.Dispose();

        tileDecalsPixels = new NativeArray<Color32>(mapSizeX * mapSizeY * PixelsPerUnit * PixelsPerUnit, Allocator.Persistent);
        CreateDynamicTexture();
    }

    public void UpdateDecalRenderer()
    {
        if (rendererCamera.orthographicSize != tempCameraSize)
            CreateDynamicTexture();
    }

    public void PrintDecal(float2 direction, float2 offset, Texture2D decal)
    {
        float angle = Vector2.Angle(Vector2.right, direction);
        if (direction.y < 0.0f)
            angle = 360 - angle;

        #region calculateStartAndSubValues
        float cos = math.cos(angle / 180.0f * pi);
        float sin = math.sin(angle / 180.0f * pi);

        //matrix: rotateX + rotateY + offset
        float2x3 matrix = GetRotationMatrix(sin, cos, offset);

        float pivotX = decal.width / 2.0f;
        float pivotY = decal.height / 2.0f;

        // for global coords in pixels
        //here point (-pivotXold, -pivotYold) ==  its (x0,y0) "koordinati otnositel'no starogo pivota"
        float x0New = GetRotationAxisX(sin, cos, -pivotX, -pivotY) + matrix.c2.x;
        float y0New = GetRotationAxisY(sin, cos, -pivotX, -pivotY) + matrix.c2.y;

        //!!Note: as we imit and rotate decal in pivot so (x0,y0) is negative relative to pivot
        //but we draw texture from 0 to size => so i add + pivotX,Y coords here
        float tempXline = x0New + pivotX;
        float tempYline = y0New + pivotY;
        #endregion
        // 0 = xLeft, 1 = xRight, 2 = yDown, 3 = yUp  *in pixels
        int[] bounds = CalculateBounds(tempXline, tempYline, cos, sin, pivotX, pivotY, matrix.c2.x, matrix.c2.y);
        NativeArray<Color32> tempTexture = new NativeArray<Color32>((bounds[1] - bounds[0]) * (bounds[3] - bounds[2]), Allocator.Persistent);

        SetTileDecalsPixels(tempTexture, RotateDecal());
        tempTexture.Dispose();

        unsafe JobHandle RotateDecal()
        {
            NativeArray<Color32> decalData = decal.GetRawTextureData<Color32>();

            RotateDecalJob job = new RotateDecalJob()
            {
                OffsetMatrix = matrix,
                TempTexturePointer = (Color32*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(tempTexture),
                TempTextureStartPoint = new int2(bounds[0], bounds[2]),
                TempTextureWidth = bounds[1] - bounds[0],
                TempTextureLength = tempTexture.Length,
                DecalPointer = (Color32*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(decalData),
                DecalStartPoint = new float2(tempXline, tempYline),
                DecalWidth = decal.width
            };

            return job.Schedule(decalData.Length, 32);
        }
        unsafe void SetTileDecalsPixels(NativeArray<Color32> colorBoundTemp, JobHandle dependency)
        {
            BakeToTileDecalsJob job = new BakeToTileDecalsJob()
            {
                MapSize = new int2(mapSizeX, mapSizeY),
                MapZeroPoint = mapZeroPoint,
                TileSize = PixelsPerUnit,
                Offset = offset,
                ColorBoundTempPtr = (Color32*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(colorBoundTemp),
                IsFloorPtr = (bool*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(isFloorPositions),
                TileDecalsPixelsPtr = (Color32*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(tileDecalsPixels),
                TempTextureWidth = bounds[1] - bounds[0],
                TempTextureHeight = bounds[3] - bounds[2],
            };

            JobHandle jh = job.Schedule(colorBoundTemp.Length, 32, dependency);
            jh.Complete();
        }
    }

    public unsafe JobHandle RenderVisibleDecals()
    {
        RenderDecalsJob job = new RenderDecalsJob()
        {
            TileSize = PixelsPerUnit,
            DynamicRendererPtr = (Color32*)NativeArrayUnsafeUtility.GetUnsafePtr(tempTexture.GetRawTextureData<Color32>()),
            AllMapTilesPtr = (Color32*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(tileDecalsPixels),
            MapSize = new int2(mapSizeX, mapSizeY),
            LowerPixel = lowerPixel,
            DynamicTextureWidth = tempTexture.width
        };

        return job.Schedule(tempTexture.height, 16);
    }

    private void UpdateDynamicRendererBorders()
    {
        tempCameraPosition = rendererCamera.transform.position;
        float3 p0 = rendererCamera.ViewportToWorldPoint(new Vector3(0, 0, 0));
        float3 p1 = rendererCamera.ViewportToWorldPoint(new Vector3(1, 1, 0));

        ClampBorders(ref p0, ref p1);

        lowerPixel = (int2)((p0.xy - dynamicTextureOversize) * PixelsPerUnit);
        upperPixel = (int2)((p1.xy + dynamicTextureOversize) * PixelsPerUnit);

        if (tempCameraSize % (2f / PixelsPerUnit) != 0)
        {
            upperPixel.x += (upperPixel.x - lowerPixel.x) % 2f == 0 ? 0 : 1;
            upperPixel.y += (upperPixel.y - lowerPixel.y) % 2f == 0 ? 0 : 1;
        }

        float posX = (upperPixel.x + lowerPixel.x) / 2f;
        float posY = (upperPixel.y + lowerPixel.y) / 2f;

        oneRenderer.transform.position = new(posX / PixelsPerUnit, posY / PixelsPerUnit);
    }

    private void ClampBorders(ref float3 p0, ref float3 p1)
    {
        //clamp renderer size X
        float factRenderSizeX = p1.x - p0.x;
        if (factRenderSizeX > mapSizeX)
        {
            p1.x = mapSizeX;
            p0.x = mapZeroPoint;
        }
        else
        {//clamp renderer position X
            if (p0.x < mapZeroPoint)
            {
                p1.x += mapZeroPoint - p0.x;
                p0.x = mapZeroPoint;
            }

            if (p1.x > mapSizeX)
            {
                p0.x -= p1.x - mapSizeX;
                p1.x = mapSizeX;
            }
        }

        //clamp renderer size Y
        float factRenderSizeY = p1.y - p0.y;
        if (factRenderSizeY > mapSizeY)
        {
            p1.y = mapSizeY;
            p0.y = mapZeroPoint;
        }
        else
        {//clamp renderer position Y
            if (p0.y < mapZeroPoint)
            {
                p1.y += mapZeroPoint - p0.y;
                p0.y = mapZeroPoint;
            }

            if (p1.y > mapSizeY)
            {
                p0.y -= p1.y - mapSizeY;
                p1.y = mapSizeY;
            }
        }
    }

    //find 4 points of texture +- weight-height(look in my paper) //in global
    private int[] CalculateBounds(float x0New, float y0New, float cos, float sin, float pivotX, float pivotY, float offsetX, float offsetY)
    {
        float p1X = x0New;
        float p1Y = y0New;

        float p2X = GetRotationAxisX(sin, cos, pivotX, -pivotY) + pivotX + offsetX;
        float p2Y = GetRotationAxisY(sin, cos, pivotX, -pivotY) + pivotY + offsetY;

        float p3X = GetRotationAxisX(sin, cos, pivotX, pivotY) + pivotX + offsetX;
        float p3Y = GetRotationAxisY(sin, cos, pivotX, pivotY) + pivotY + offsetY;

        float p4X = GetRotationAxisX(sin, cos, -pivotX, pivotY) + pivotX + offsetX;
        float p4Y = GetRotationAxisY(sin, cos, -pivotX, pivotY) + pivotY + offsetY;

        return new int[4]
            {   Mathf.FloorToInt(Mathf.Min(p1X,p2X,p3X,p4X)),
                Mathf.CeilToInt(Mathf.Max(p1X,p2X,p3X,p4X)),
                Mathf.FloorToInt(Mathf.Min(p1Y,p2Y,p3Y,p4Y)),
                Mathf.CeilToInt(Mathf.Max(p1Y,p2Y,p3Y,p4Y))     };
    }

    private float2x3 GetRotationMatrix(float sin, float cos, Vector2 offset)
    {
        float dx_x = GetRotationAxisX(sin, cos, 1.0f, 0.0f);
        float dx_y = GetRotationAxisY(sin, cos, 1.0f, 0.0f);
        float dy_x = GetRotationAxisX(sin, cos, 0.0f, 1.0f);
        float dy_y = GetRotationAxisY(sin, cos, 0.0f, 1.0f);

        float offset_x = offset.x * PixelsPerUnit;
        float offset_y = offset.y * PixelsPerUnit;

        return new float2x3(new(dx_x, dx_y), new(dy_x, dy_y), new(offset_x, offset_y));
    }

    private float GetRotationAxisX(float sin, float cos, float x, float y) => (x * cos + y * (-sin));
    private float GetRotationAxisY(float sin, float cos, float x, float y) => (x * sin + y * cos);

    public unsafe JobHandle CleanTileDecalInRange(float2 position, float range, float cleaningQuality)
    {
        int2 centerPixelPoint = (int2)(position * PixelsPerUnit);
        int rangeInt = (int)(1.5f * range * PixelsPerUnit);

        CleanRangeTileDecalJob2 job = new CleanRangeTileDecalJob2()
        {
            TileDecalsPixelsPtr = (Color32*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(tileDecalsPixels),
            CenterPixelPoint = centerPixelPoint,
            TileSize = PixelsPerUnit,
            MapSizeX = mapSizeX,
            CritterRange = rangeInt,
            FullCleanRange = (int)(rangeInt * cleaningQuality)
        };

        return job.Schedule(4 * rangeInt * rangeInt, 32);
    }

    public unsafe void AddGaussDecal(float2 position, float2 direction, Texture2D decalTexture)
    {
        EntityManager manager = World.DefaultGameObjectInjectionWorld.EntityManager;
        EntityQuery locatorQuery = manager.CreateEntityQuery(new ComponentType[] { typeof(ObstaclesLocator) });
        ObstaclesLocator obstaclesLocator = locatorQuery.GetSingleton<ObstaclesLocator>();
        obstaclesLocator.RaycastObstacle(position, position + mapSizeX * mapSizeY * direction, out QuadtreeRaycastHit<ObstacleInfo> obstacleHit, default);

        float sizeX = math.abs(obstacleHit.point.x - position.x);
        float sizeY = math.abs(obstacleHit.point.y - position.y);

        float2 step = direction / math.abs(sizeX > sizeY ? direction.x : direction.y);
        int stepCount = (int)(sizeX > sizeY ? math.abs(obstacleHit.point.x - position.x) * PixelsPerUnit : math.abs(obstacleHit.point.y - position.y) * PixelsPerUnit);

        var job = new CleanLineTileDecalJob()
        {
            TileDecalsPixelsPtr = (Color32*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(tileDecalsPixels),
            TexturePixelsPtr = (Color32*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(decalTexture.GetRawTextureData<Color32>()),
            TextureSizeX = decalTexture.width,
            TextureSizeHalfY = decalTexture.height / 2,
            IsFloorPtr = (bool*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(isFloorPositions),
            MapSizeX = mapSizeX,
            TileDecalsLength = tileDecalsPixels.Length,
            TileSize = PixelsPerUnit,
            StartPointPix = (int2)(position * PixelsPerUnit),
            StepDirection = step,
            BetterNormalAxis = sizeX > sizeY ? new int2(0, 1) : new int2(1, 0),
            Range = (int)(gaussClearRange * PixelsPerUnit),
            FullClearRange = (int)(gaussFullClearMult * gaussClearRange * PixelsPerUnit),
            ObstacleHitPoint = (int2)(obstacleHit.point * PixelsPerUnit)
        };

        JobHandle jh = job.Schedule(stepCount, 32);
        jh.Complete();
    }
}


[BurstCompile(CompileSynchronously = true)]
public unsafe struct CleanRangeTileDecalJob2 : IJobParallelFor
{
    [ReadOnly, NativeDisableUnsafePtrRestriction] public Color32* TileDecalsPixelsPtr;
    [ReadOnly] public int2 CenterPixelPoint;
    [ReadOnly] public int TileSize;
    [ReadOnly] public int MapSizeX;
    [ReadOnly] public int CritterRange;
    [ReadOnly] public int FullCleanRange;

    public void Execute(int i)
    {
        if (CritterRange == 0)
            CritterRange = 1;

        int x = i % (2 * CritterRange) + CenterPixelPoint.x - CritterRange;
        int y = (i / (2 * CritterRange)) + CenterPixelPoint.y - CritterRange;

        int pixelPosInArray = x + y * MapSizeX * TileSize;
        Color32 clr = *(TileDecalsPixelsPtr + pixelPosInArray);

        float dist = Distance2d(CenterPixelPoint, x, y);
        if (dist <= FullCleanRange)
            *(TileDecalsPixelsPtr + pixelPosInArray) = Color.clear;
        else
            if (dist < CritterRange)
        {
            float targetAlfa = (dist - FullCleanRange) / (CritterRange - FullCleanRange) * 255.0f;
            if (clr.a < targetAlfa)
                return;

            clr.a = (byte)(targetAlfa);
            *(TileDecalsPixelsPtr + pixelPosInArray) = clr;
        }
    }
    private float Distance2d(float2 center, int x, int y) => math.sqrt((center.x - x) * (center.x - x) + (center.y - y) * (center.y - y));
}



//[BurstCompile(CompileSynchronously = true)]
//public unsafe partial struct CleanRangeTileDecalJob : IJobEntity
//{
//    [NativeDisableUnsafePtrRestriction] public Color32* TileDecalsPixelsPtr;

//    [ReadOnly] public int MapSizeX;


//    public void Execute(CritterComponent critter, PositionComponent position)
//    {
//        int2 centerPixelPoint = (int2)(position.Position * TileDecalSystem.PixelsPerUnit);
//        int critterRange = (int)(1.5f * critter.Radius * TileDecalSystem.PixelsPerUnit);
//        int fullCleanRange = (int)(critterRange * critter.CleaningQuality);
//        int fullCleanRangeSquared = fullCleanRange * fullCleanRange;
//        int maxCounts = critterRange * critterRange * 4;

//        for (int i = 0; i < maxCounts; ++i)
//        {
//            int absX = i % (2 * critterRange) - critterRange;
//            int absY = i / (2 * critterRange) - critterRange;

//            int x = absX + centerPixelPoint.x;
//            int y = absY + centerPixelPoint.y;
//            int pixelPosInArray = x + y * MapSizeX * TileDecalSystem.PixelsPerUnit;
//            int distSqr = absX * absX + absY * absY;

//            if (distSqr <= fullCleanRangeSquared)
//                *(TileDecalsPixelsPtr + pixelPosInArray) = Color.clear;
//            else if (distSqr < critterRange * critterRange)
//            {
//                float dist = math.sqrt(distSqr);
//                Color32 clr = *(TileDecalsPixelsPtr + pixelPosInArray);

//                float targetAlfa = (dist - fullCleanRange) / (critterRange - fullCleanRange) * 255.0f;

//                if (clr.a < targetAlfa)
//                    continue;

//                clr.a = (byte)(targetAlfa);
//                *(TileDecalsPixelsPtr + pixelPosInArray) = clr;
//            }


//        }
//    }
//    private float Distance2d(float2 center, int x, int y) => math.sqrt((center.x - x) * (center.x - x) + (center.y - y) * (center.y - y));
//}

[BurstCompile(CompileSynchronously = true)]
public unsafe struct CleanLineTileDecalJob : IJobParallelFor
{
    [ReadOnly, NativeDisableUnsafePtrRestriction] public Color32* TileDecalsPixelsPtr;
    [ReadOnly, NativeDisableUnsafePtrRestriction] public Color32* TexturePixelsPtr;
    [ReadOnly, NativeDisableUnsafePtrRestriction] public bool* IsFloorPtr;

    [ReadOnly] public int MapSizeX;
    [ReadOnly] public int TileDecalsLength;
    [ReadOnly] public int TileSize;

    [ReadOnly] public int TextureSizeX;
    [ReadOnly] public int TextureSizeHalfY;

    [ReadOnly] public int2 StartPointPix;
    [ReadOnly] public float2 StepDirection;
    [ReadOnly] public int2 BetterNormalAxis;
    [ReadOnly] public int2 ObstacleHitPoint;
    [ReadOnly] public int Range;
    [ReadOnly] public int FullClearRange;

    public void Execute(int i)
    {
        int x = StartPointPix.x + (int)(i * StepDirection.x);
        int y = StartPointPix.y + (int)(i * StepDirection.y);

        int texturePosX = i % TextureSizeX;

        int x2, y2;

        for (int r = 0; r < Range; r++)
        {
            if (r < FullClearRange)
            {
                x2 = r * BetterNormalAxis.x;
                y2 = r * BetterNormalAxis.y;

                if (r < TextureSizeHalfY)
                {
                    SetPixels(x + x2, y + y2, texturePosX, r);
                    SetPixels(x - x2, y - y2, texturePosX, -r);
                }
                else
                {
                    ClearPixel(x + x2, y + y2);
                    ClearPixel(x - x2, y - y2);
                }
            }
            else
            {
                float targetAlfa = (r - (float)FullClearRange) / (Range - FullClearRange) * 255.0f;
                x2 = r * BetterNormalAxis.x;
                y2 = r * BetterNormalAxis.y;

                FadePixel(x + x2, y + y2, targetAlfa);
                FadePixel(x - x2, y - y2, targetAlfa);
            }
        }
    }

    private void SetPixels(int x2, int y2, int decalTexturePosX, int r)
    {
        if (((StepDirection.x > 0) == (x2 > ObstacleHitPoint.x)) || ((StepDirection.y > 0) == (y2 > ObstacleHitPoint.y)))
            return;

        int pixelPosInArray = x2 + y2 * MapSizeX * TileSize;
        Color32 clr = *(TileDecalsPixelsPtr + pixelPosInArray);
        if (clr.a == 0)
            return;

        if (pixelPosInArray > 0 && pixelPosInArray < TileDecalsLength && TileSize != 0 && IsFloorInside(x2 / TileSize, y2 / TileSize))
        {
            Color32 textureClr = *(TexturePixelsPtr + decalTexturePosX + (TextureSizeHalfY + r) * TextureSizeX);
            textureClr.a = (byte)math.min((int)textureClr.a, (int)clr.a);

            *(TileDecalsPixelsPtr + pixelPosInArray) = textureClr;
        }
    }

    private void ClearPixel(int x2, int y2)
    {
        if (((StepDirection.x > 0) == (x2 > ObstacleHitPoint.x)) || ((StepDirection.y > 0) == (y2 > ObstacleHitPoint.y)))
            return;

        int pixelPosInArray = x2 + y2 * MapSizeX * TileSize;
        Color32 clr = *(TileDecalsPixelsPtr + pixelPosInArray);
        if (clr.a == 0)
            return;

        if (pixelPosInArray > 0 && pixelPosInArray < TileDecalsLength && TileSize != 0 && IsFloorInside(x2 / TileSize, y2 / TileSize))
            *(TileDecalsPixelsPtr + pixelPosInArray) = Color.clear;
    }

    private void FadePixel(int x2, int y2, float targetAlfa)
    {
        if (((StepDirection.x > 0) == (x2 > ObstacleHitPoint.x)) || ((StepDirection.y > 0) == (y2 > ObstacleHitPoint.y)))
            return;

        int pixelPosInArray = x2 + y2 * MapSizeX * TileSize;
        Color32 clr = *(TileDecalsPixelsPtr + pixelPosInArray);

        if (clr.a < targetAlfa)
            return;

        Color32 darkedClr = Color.Lerp(Color.black, clr, targetAlfa / 2 / 255);
        darkedClr.a = (byte)(targetAlfa);

        if (pixelPosInArray > 0 && pixelPosInArray < TileDecalsLength && TileSize != 0 && IsFloorInside(x2 / TileSize, y2 / TileSize))
            *(TileDecalsPixelsPtr + pixelPosInArray) = darkedClr;
    }

    private bool IsFloorInside(int x, int y) => *(IsFloorPtr + x + y * MapSizeX);
}

[BurstCompile(CompileSynchronously = true)]
public unsafe struct RenderDecalsJob : IJobParallelFor
{
    [ReadOnly] public int TileSize;
    [ReadOnly, NativeDisableUnsafePtrRestriction] public Color32* AllMapTilesPtr;
    [ReadOnly] public int2 MapSize;

    [ReadOnly, NativeDisableUnsafePtrRestriction] public Color32* DynamicRendererPtr;
    [ReadOnly] public int DynamicTextureWidth;
    [ReadOnly] public int2 LowerPixel;

    // for i = RendererPixelsLine
    public void Execute(int lineIndex)
    {
        int rendererLineOffset = lineIndex * DynamicTextureWidth;
        int mapDecalsLineOffset = (LowerPixel.y + lineIndex) * MapSize.x * TileSize + LowerPixel.x;
        //if AllMapTilesPtr is out of bounds + magic offset
        if (mapDecalsLineOffset < 0 || mapDecalsLineOffset >= MapSize.y * MapSize.x * (TileSize - 1) * (TileSize - 1) || rendererLineOffset < 5)
            mapDecalsLineOffset = 0;

        UnsafeUtility.MemCpy(DynamicRendererPtr + rendererLineOffset, AllMapTilesPtr + mapDecalsLineOffset, DynamicTextureWidth * (long)UnsafeUtility.SizeOf<Color32>());
    }
}

[BurstCompile(CompileSynchronously = true)]
public unsafe struct BakeToTileDecalsJob : IJobParallelFor
{
    [ReadOnly] public int2 MapSize;
    [ReadOnly] public int MapZeroPoint;
    [ReadOnly] public int TileSize;

    [ReadOnly] public float2 Offset;
    [ReadOnly, NativeDisableUnsafePtrRestriction] public Color32* ColorBoundTempPtr;

    [ReadOnly, NativeDisableUnsafePtrRestriction] public bool* IsFloorPtr;
    [ReadOnly, NativeDisableUnsafePtrRestriction] public Color32* TileDecalsPixelsPtr;
    [ReadOnly] public int TempTextureWidth;
    [ReadOnly] public int TempTextureHeight;

    // for i = each TempTexturePixel
    public void Execute(int i)
    {
        Color32 tempPixelColor = *(ColorBoundTempPtr + i);
        if (tempPixelColor.a == 0)
            return;

        int pixelXGlobal = i % TempTextureWidth + (int)(Offset.x * TileSize - TempTextureWidth / 2);
        int pixelYGlobal = i / TempTextureWidth + (int)(Offset.y * TileSize - TempTextureHeight / 2);

        if (IsPixelOutsideMap(pixelXGlobal, pixelYGlobal))
            return;
        int pixelPosInArray = pixelXGlobal + pixelYGlobal * MapSize.x * TileSize;

        if (TileSize != 0 && !IsFloorInside(pixelXGlobal / TileSize, pixelYGlobal / TileSize))
        {
            *(TileDecalsPixelsPtr + pixelPosInArray) = Color.clear;
            return;
        }
        Color32 oldColor = *(TileDecalsPixelsPtr + pixelPosInArray);

        *(TileDecalsPixelsPtr + pixelPosInArray) = AlphaBlendColors(oldColor, tempPixelColor);
    }

    private bool IsPixelOutsideMap(int x, int y) => (x < MapZeroPoint || x >= MapSize.x * TileSize || y < MapZeroPoint || y >= MapSize.y * TileSize);
    private bool IsFloorInside(int x, int y) => *(IsFloorPtr + x + y * MapSize.x);
    private Color32 AlphaBlendColors(Color32 oldColor, Color32 inputColor, float inputAlfaStrength = 1)
    {
        float old_a = oldColor.a / 255.0f;
        float old_r_a = oldColor.r / 255.0f * old_a;
        float old_g_a = oldColor.g / 255.0f * old_a;
        float old_b_a = oldColor.b / 255.0f * old_a;

        float inp_a = inputColor.a * inputAlfaStrength / 255.0f;
        float inp_r_a = inputColor.r / 255.0f * inp_a;
        float inp_g_a = inputColor.g / 255.0f * inp_a;
        float inp_b_a = inputColor.b / 255.0f * inp_a;

        float new_r_a = inp_r_a + old_r_a * (1.0f - inp_a);
        float new_g_a = inp_g_a + old_g_a * (1.0f - inp_a);
        float new_b_a = inp_b_a + old_b_a * (1.0f - inp_a);
        float new_a = inp_a + old_a * (1.0f - inp_a);

        float new_r = new_r_a / new_a;
        float new_g = new_g_a / new_a;
        float new_b = new_b_a / new_a;

        byte colR = (byte)math.round(math.clamp(new_r * 255.0f, 0.0f, 255.0f));
        byte colG = (byte)math.round(math.clamp(new_g * 255.0f, 0.0f, 255.0f));
        byte colB = (byte)math.round(math.clamp(new_b * 255.0f, 0.0f, 255.0f));
        byte colA = (byte)math.round(math.clamp(new_a * 255.0f, 0.0f, 255.0f));

        return new Color32(colR, colG, colB, colA);
    }
}

[BurstCompile(CompileSynchronously = true)]
public unsafe struct RotateDecalJob : IJobParallelFor
{
    [ReadOnly] public float2x3 OffsetMatrix;

    [ReadOnly, NativeDisableUnsafePtrRestriction] public Color32* TempTexturePointer;
    [ReadOnly] public int2 TempTextureStartPoint;
    [ReadOnly] public int TempTextureWidth;
    [ReadOnly] public int TempTextureLength;

    [ReadOnly, NativeDisableUnsafePtrRestriction] public Color32* DecalPointer;
    [ReadOnly] public float2 DecalStartPoint;
    [ReadOnly] public int DecalWidth;

    // for i = each DecalPixel
    public void Execute(int i)
    {
        float xNew = DecalStartPoint.x + (OffsetMatrix.c0.x * (i % DecalWidth)) + (OffsetMatrix.c1.x * i / DecalWidth);
        float yNew = DecalStartPoint.y + (OffsetMatrix.c0.y * (i % DecalWidth)) + (OffsetMatrix.c1.y * i / DecalWidth);

        Color32 tempPixelColor = *(DecalPointer + i);

        if (tempPixelColor.a == 0)
            return;

        UpdateNeighborPixels(xNew - TempTextureStartPoint.x, yNew - TempTextureStartPoint.y, tempPixelColor);
    }

    private void UpdateNeighborPixels(float xNew, float yNew, Color32 color)
    {
        int4x2 pixels = new int4x2((int)math.floor(xNew), (int)math.floor(yNew),
                                    (int)math.ceil(xNew), (int)math.floor(yNew),
                                    (int)math.floor(xNew), (int)math.ceil(yNew),
                                    (int)math.ceil(xNew), (int)math.ceil(yNew));

        for (int i = 0; i < 4; i++)
        {
            int posInArray = pixels.c0[i] + pixels.c1[i] * TempTextureWidth;

            if (posInArray < 0 || posInArray >= TempTextureLength)
                continue;

            float percent = math.abs(xNew - pixels.c0[i] * math.abs(yNew - pixels.c1[i]));
            Color32 oldColor = *(TempTexturePointer + posInArray);

            *(TempTexturePointer + posInArray) = Color32.Lerp(oldColor, color, percent);
        }
    }
}

// use this if need Resize Texture2D (check optimize)

//private Texture2D Resize(Texture2D texture2D, int targetX, int targetY)
//{
//    RenderTexture rt = new RenderTexture(targetX, targetY, 1,RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
//    RenderTexture.active = rt;
//    Graphics.Blit(texture2D, rt);
//    Texture2D result = new Texture2D(targetX, targetY, TextureFormat.RGBA32, false);
//    result.ReadPixels(new Rect(0,0, targetX, targetY),0,0);
//    result.Apply();

//    return result;
//}