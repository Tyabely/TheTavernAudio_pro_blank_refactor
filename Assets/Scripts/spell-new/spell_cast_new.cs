using UnityEngine;
using FMOD.Studio; // PLAYBACK_STATE
using FMODUnity;   // RuntimeManager, RuntimeUtils

// Rzucanie zaklęcia z kamery: ładowanie (LPM trzymany), pętla po naładowaniu,
// krótki zamach (Casting) i wystrzał po puszczeniu LPM,
// anulowanie (PPM albo puszczenie LPM przed naładowaniem).
public class spell_cast_new : MonoBehaviour
{
    [Header("Zaklęcie")]
    public GameObject spellPrefab;
    public Transform spellSpawnPoint;
    public float chargeTime = 2f;
    public float spellShootForce = 20f;

    [Header("Trajektoria: ładowanie (Charge)")]
    [Tooltip("Jak nisko pod punktem dłoni orb zaczyna wznoszenie (m).")]
    public float chargeRiseHeight = 0.5f;
    [Tooltip("Jak daleko orb buja się na boki podczas wznoszenia (m).")]
    public float chargeWobbleWidth = 0.06f;
    [Tooltip("Ile pełnych wychyleń lewo-prawo w trakcie ładowania.")]
    public float chargeWobbleCount = 1.5f;

    [Header("Trajektoria: pętla po naładowaniu (Charged Loop)")]
    [Tooltip("Czas jednego okrążenia. Pętle na zmianę: v1, v2, v1...")]
    public float loopDuration = 1.5f;
    [Tooltip("Pół-osie elipsy w płaszczyźnie ekranu (szerokość, wysokość).")]
    public Vector2 loopRadius = new Vector2(0.35f, 0.08f);
    [Tooltip("Jak daleko orb wychodzi do przodu/do tyłu na okrążeniu.")]
    public float loopDepth = 0.15f;
    [Tooltip("Nachylenie pętli v1 (stopnie, + = prawa strona wyżej).")]
    public float loopTiltV1 = 15f;
    [Tooltip("Nachylenie pętli v2 (stopnie, - = prawa strona niżej).")]
    public float loopTiltV2 = -20f;
    public Vector3 loopCenterV1 = Vector3.zero;
    public Vector3 loopCenterV2 = new Vector3(0.05f, 0.05f, 0f);
    [Tooltip("Jaka część okrążenia płynnie przechodzi w kolejny wariant.")]
    [Range(0f, 1f)] public float loopBlend = 0.3f;
    [Tooltip("Lekkie bujanie nachylenia pętli (stopnie).")]
    public float loopDriftAngle = 6f;
    [Tooltip("Czas płynnego wejścia z ładowania w pętlę.")]
    public float loopEntryTime = 0.3f;

    [Header("Rzut (Casting)")]
    [Tooltip("Ile trwa zamach po puszczeniu LPM, zanim pocisk poleci.")]
    public float castDuration = 0.2f;
    [Tooltip("O ile orb przesuwa się (hamując) w trakcie zamachu, lokalnie dla dłoni.")]
    public Vector3 castOffset = new Vector3(0f, 0.08f, 0.15f);

    [Header("Kolory orba (Purple Main + iskry)")]
    public Color chargeStartColor = new Color(0f, 1f, 1f);
    public Color chargeEndColor = new Color(0.25f, 0.12f, 0.75f);
    public Color chargedColor = new Color(0.878f, 0.145f, 0.794f);
    [Tooltip("Ile sekund po naładowaniu kolor przechodzi do chargedColor.")]
    public float chargedColorTime = 1f;
    public AnimationCurve chargeCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

    [Header("Czarny dym spod dłoni")]
    [Tooltip("Jak nisko pod punktem dłoni rodzi się dym. Wierzch dymu sięga wysokości orba.")]
    public float handSmokeDrop = 0.65f;
    public float handSmokeRateCharging = 20f;
    public float handSmokeRateCharged = 45f;
    public float handSmokeSpeedCharging = 0.25f;
    public float handSmokeSpeedCharged = 0.5f;

    [Header("Zapasowe nazwy dzieci (gdy prefab nie ma spell_new z przypisanymi polami)")]
    public string chargeChildFallbackName = "Charge_FX";
    public string chargedChildFallbackName = "Charged_FX";
    public string handSmokeChildFallbackName = "Hand_Smoke_FX";

    private enum SpellState { Idle, Charging, Holding, Casting }
    private SpellState currentState = SpellState.Idle;
    private float chargeTimer = 0f;
    private float holdTimer = 0f;
    private float castTimer = 0f;
    private Vector3 currentOffset = Vector3.zero; // pozycja orba względem dłoni
    private Vector3 castStartOffset = Vector3.zero;

    // Referencje do aktualnie ładowanego pocisku
    private GameObject currentSpellInstance;
    private Rigidbody currentSpellRb;
    private Collider[] currentSpellColliders;
    private spell_new currentSpellLogic;
    private Transform chargeVfxTransform;
    private Vector3 chargeVfxBaseScale = Vector3.one;
    private ParticleSystem[] tintedParticleSystems = new ParticleSystem[0];
    private GameObject chargedVfx;
    private ParticleSystem handSmoke;

    // Audio FMOD
    private AudioSystem audioSystem;
    private bool isAudioInitialized = false;

    void Awake()
    {
        audioSystem = FindFirstObjectByType<AudioSystem>();

        if (audioSystem == null)
        {
            Debug.LogWarning("POMOCNIK: Nie znaleziono AudioSystem na scenie. Kontynuuję bez audio.");
            return;
        }

        // EventReference.Path istnieje tylko w edytorze (build by się nie skompilował).
        // IsNull działa wszędzie.
        if (!audioSystem.spellEvent.IsNull)
        {
            audioSystem.SpellSound = RuntimeManager.CreateInstance(audioSystem.spellEvent);
            isAudioInitialized = true;
        }
        else
        {
            Debug.LogWarning("POMOCNIK: Brak przypisanego FMOD Eventu dla zaklęcia. Kontynuuję bez audio.");
        }
    }

    void Update()
    {
        switch (currentState)
        {
            case SpellState.Idle: HandleIdleState(); break;
            case SpellState.Charging: HandleChargingState(); break;
            case SpellState.Holding: HandleHoldingState(); break;
            case SpellState.Casting: HandleCastingState(); break;
        }

        if (isAudioInitialized)
            audioSystem.SpellSound.set3DAttributes(RuntimeUtils.To3DAttributes(transform));
    }

    // Pozycjonowanie dopiero w LateUpdate: FPSController rusza graczem i kamerą w swoim Update,
    // a kolejność Update między skryptami jest dowolna. Orb ustawiany w Update był w części klatek
    // liczony od nieaktualnej pozycji dłoni i "skakał" między dwoma miejscami (dwa orby na ekranie).
    void LateUpdate()
    {
        if (currentState == SpellState.Idle) return;

        FollowSpawnPoint();
        PlaceHandSmoke();
    }

    // ---------------- STANY ----------------

    void HandleIdleState()
    {
        if (Input.GetMouseButtonDown(0) && CanStartCast())
            StartCharging();
    }

    void HandleChargingState()
    {
        // Pocisk zniknął z zewnątrz (np. zniszczony innym skryptem)
        if (currentSpellInstance == null) { CancelCast(); return; }

        if (Input.GetMouseButtonDown(1)) { CancelCast(); return; }

        // LPM puszczony przed pełnym naładowaniem = anulowanie
        if (!Input.GetMouseButton(0)) { CancelCast(); return; }

        chargeTimer += Time.deltaTime;
        float t = chargeTime > 0f ? Mathf.Clamp01(chargeTimer / chargeTime) : 1f;

        currentOffset = GetChargeOffset(t);
        UpdateHandSmoke(handSmokeRateCharging, handSmokeSpeedCharging);
        ApplyChargeVisuals(chargeCurve.Evaluate(t), t);

        if (t >= 1f)
        {
            holdTimer = 0f;
            currentState = SpellState.Holding;
            SetFullyChargedVisuals();
        }
    }

    void HandleHoldingState()
    {
        if (currentSpellInstance == null) { CancelCast(); return; }

        if (Input.GetMouseButtonDown(1)) { CancelCast(); return; }

        holdTimer += Time.deltaTime;

        // Płynne wejście z końca ładowania (punkt dłoni) w okrążenie
        Vector3 loopOffset = GetLoopOffset(holdTimer);
        float entry = loopEntryTime > 0f ? Mathf.SmoothStep(0f, 1f, holdTimer / loopEntryTime) : 1f;
        currentOffset = Vector3.Lerp(GetChargeOffset(1f), loopOffset, entry);

        UpdateHandSmoke(handSmokeRateCharged, handSmokeSpeedCharged);

        // Przez pierwszą sekundę pętli kolor przechodzi z ciemnego fioletu w różowy fiolet
        float colorT = chargedColorTime > 0f ? Mathf.Clamp01(holdTimer / chargedColorTime) : 1f;
        SetStartColor(Color.Lerp(chargeEndColor, chargedColor, colorT));

        if (!Input.GetMouseButton(0))
            StartCasting();
    }

    void HandleCastingState()
    {
        if (currentSpellInstance == null) { CancelCast(); return; }

        castTimer += Time.deltaTime;
        float t = castDuration > 0f ? Mathf.Clamp01(castTimer / castDuration) : 1f;

        // Nagła zmiana toru: orb gwałtownie hamuje i lekko się unosi (ease-out)
        float braked = 1f - Mathf.Pow(1f - t, 3f);
        currentOffset = castStartOffset + castOffset * braked;

        UpdateHandSmoke(handSmokeRateCharged, handSmokeSpeedCharged);

        if (t >= 1f)
        {
            FireSpell();
            ResetToIdle();
        }
    }

    // ---------------- PRZEJŚCIA ----------------

    bool CanStartCast()
    {
        if (!isAudioInitialized) return true;

        // Nowe zaklęcie dopiero gdy dźwięk poprzedniego całkiem ucichnie
        audioSystem.SpellSound.getPlaybackState(out PLAYBACK_STATE state);
        return state == PLAYBACK_STATE.STOPPED;
    }

    void StartCharging()
    {
        if (!SpawnSpell()) return;

        chargeTimer = 0f;
        currentState = SpellState.Charging;

        if (isAudioInitialized)
            audioSystem.SpellCast();
    }

    void StartCasting()
    {
        // Dźwięk wypuszczenia leci od razu z puszczeniem LPM, tak jak wcześniej
        if (isAudioInitialized)
            audioSystem.SpellRelease();

        castTimer = 0f;
        castStartOffset = currentOffset;
        currentState = SpellState.Casting;
    }

    void CancelCast()
    {
        if (currentSpellInstance != null)
            Destroy(currentSpellInstance);

        if (isAudioInitialized)
        {
            audioSystem.SpellCancel();
            audioSystem.SpellRelease();
        }

        ClearSpellReferences();
        ResetToIdle();
    }

    void ResetToIdle()
    {
        chargeTimer = 0f;
        holdTimer = 0f;
        castTimer = 0f;
        currentOffset = Vector3.zero;
        currentState = SpellState.Idle;
    }

    // ---------------- TRAJEKTORIA ----------------

    // Od dołu do góry, bujając się na boki. t = 0..1, na końcu orb jest w punkcie dłoni.
    public Vector3 GetChargeOffset(float t)
    {
        float x = chargeWobbleWidth * Mathf.Sin(t * chargeWobbleCount * 2f * Mathf.PI);
        float y = -chargeRiseHeight * (1f - t);
        return new Vector3(x, y, 0f);
    }

    // Okrążenia po naładowaniu: parzyste = v1, nieparzyste = v2,
    // pod koniec każdego okrążenia nachylenie i środek płynnie przechodzą w kolejny wariant.
    public Vector3 GetLoopOffset(float loopTime)
    {
        float duration = Mathf.Max(0.01f, loopDuration);
        float cycle = loopTime / duration;
        int index = Mathf.FloorToInt(cycle);
        float frac = cycle - index;
        bool isV2 = (index % 2) == 1;

        float blend = loopBlend > 0f ? Mathf.SmoothStep(0f, 1f, (frac - (1f - loopBlend)) / loopBlend) : 0f;

        float tiltFrom = isV2 ? loopTiltV2 : loopTiltV1;
        float tiltTo = isV2 ? loopTiltV1 : loopTiltV2;
        Vector3 centerFrom = isV2 ? loopCenterV2 : loopCenterV1;
        Vector3 centerTo = isV2 ? loopCenterV1 : loopCenterV2;

        float tilt = Mathf.Lerp(tiltFrom, tiltTo, blend) + loopDriftAngle * Mathf.Sin(loopTime * 0.8f);
        Vector3 center = Vector3.Lerp(centerFrom, centerTo, blend);

        float phase = cycle * 2f * Mathf.PI;
        Vector2 p = new Vector2(Mathf.Cos(phase) * loopRadius.x, Mathf.Sin(phase) * loopRadius.y);
        float rad = tilt * Mathf.Deg2Rad;
        Vector2 rotated = new Vector2(
            p.x * Mathf.Cos(rad) - p.y * Mathf.Sin(rad),
            p.x * Mathf.Sin(rad) + p.y * Mathf.Cos(rad));

        return center + new Vector3(rotated.x, rotated.y, Mathf.Sin(phase) * loopDepth);
    }

    Vector3 OffsetToWorld(Vector3 offset)
    {
        // Bez skali dłoni, żeby wymiary trajektorii były w metrach
        return spellSpawnPoint.position + spellSpawnPoint.rotation * offset;
    }

    // ---------------- POCISK ----------------

    bool SpawnSpell()
    {
        if (spellPrefab == null || spellSpawnPoint == null)
        {
            Debug.LogWarning("POMOCNIK: Brak spellPrefab albo spellSpawnPoint w spell_cast_new.");
            return false;
        }

        currentOffset = GetChargeOffset(0f);
        currentSpellInstance = Instantiate(spellPrefab, OffsetToWorld(currentOffset), spellSpawnPoint.rotation);

        // Podczas ładowania fizyka nie rusza pocisku...
        currentSpellRb = currentSpellInstance.GetComponent<Rigidbody>();
        if (currentSpellRb != null)
            currentSpellRb.isKinematic = true;

        // ...a collidery są wyłączone, żeby nie trącał gracza ani otoczenia
        currentSpellColliders = currentSpellInstance.GetComponentsInChildren<Collider>();
        foreach (Collider c in currentSpellColliders)
            c.enabled = false;

        currentSpellLogic = currentSpellInstance.GetComponent<spell_new>();

        // Efekt ładowania (orb): najpierw pole ze spell_new, w razie braku szukamy po nazwie
        GameObject chargeObj = null;
        if (currentSpellLogic != null && currentSpellLogic.chargeChild != null)
            chargeObj = currentSpellLogic.chargeChild;
        else
            chargeObj = FindChild(chargeChildFallbackName);

        if (chargeObj != null)
        {
            // Skalujemy tylko efekt ładowania, z zachowaniem jego oryginalnej skali
            chargeVfxTransform = chargeObj.transform;
            chargeVfxBaseScale = chargeVfxTransform.localScale;
            chargeVfxTransform.localScale = Vector3.zero;

            // Barwimy wszystkie particle orba (rdzeń i iskry)
            tintedParticleSystems = chargeObj.GetComponentsInChildren<ParticleSystem>();
        }
        SetStartColor(chargeStartColor);

        // Efekt "naładowany" (ciemnoszary dym): wyłączony do momentu pełnego naładowania
        if (currentSpellLogic != null && currentSpellLogic.chargedChild != null)
            chargedVfx = currentSpellLogic.chargedChild;
        else
            chargedVfx = FindChild(chargedChildFallbackName);

        if (chargedVfx != null)
            chargedVfx.SetActive(false);

        // Czarny dym spod dłoni: stoi przy dłoni, nie lata z orbem
        GameObject handSmokeObj = null;
        if (currentSpellLogic != null && currentSpellLogic.handSmokeChild != null)
            handSmokeObj = currentSpellLogic.handSmokeChild;
        else
            handSmokeObj = FindChild(handSmokeChildFallbackName);

        if (handSmokeObj != null)
            handSmoke = handSmokeObj.GetComponent<ParticleSystem>();

        PlaceHandSmoke();
        UpdateHandSmoke(handSmokeRateCharging, handSmokeSpeedCharging);
        return true;
    }

    GameObject FindChild(string childName)
    {
        if (string.IsNullOrEmpty(childName) || currentSpellInstance == null) return null;
        Transform child = currentSpellInstance.transform.Find(childName);
        return child != null ? child.gameObject : null;
    }

    void FollowSpawnPoint()
    {
        if (currentSpellInstance == null || spellSpawnPoint == null) return;

        currentSpellInstance.transform.SetPositionAndRotation(OffsetToWorld(currentOffset), spellSpawnPoint.rotation);
    }

    void PlaceHandSmoke()
    {
        if (handSmoke == null || spellSpawnPoint == null) return;

        // Dziecko poruszającego się pocisku, więc co klatkę wracamy pod dłoń
        handSmoke.transform.SetPositionAndRotation(
            OffsetToWorld(new Vector3(0f, -handSmokeDrop, 0f)), spellSpawnPoint.rotation);
    }

    void UpdateHandSmoke(float rate, float speed)
    {
        if (handSmoke == null) return;

        var emission = handSmoke.emission;
        emission.rateOverTime = rate;

        // Czas życia dobrany tak, żeby wierzch dymu kończył się na wysokości orba
        var main = handSmoke.main;
        main.startSpeed = speed;
        main.startLifetime = speed > 0f ? handSmokeDrop / speed : 1f;
    }

    void ApplyChargeVisuals(float scaleValue, float t)
    {
        if (chargeVfxTransform != null)
            chargeVfxTransform.localScale = chargeVfxBaseScale * scaleValue;

        SetStartColor(Color.Lerp(chargeStartColor, chargeEndColor, t));
    }

    void SetFullyChargedVisuals()
    {
        if (chargeVfxTransform != null)
            chargeVfxTransform.localScale = chargeVfxBaseScale;

        SetStartColor(chargeEndColor);

        if (chargedVfx != null)
            chargedVfx.SetActive(true);
    }

    void SetStartColor(Color color)
    {
        foreach (ParticleSystem ps in tintedParticleSystems)
        {
            if (ps == null) continue;
            var main = ps.main;
            main.startColor = color;
        }
    }

    void FireSpell()
    {
        if (currentSpellInstance == null) return;

        // Wystrzał z końcowej pozycji zamachu
        FollowSpawnPoint();

        // Dym "naładowany" i dym z dłoni są tylko na czas ładowania/trzymania
        if (chargedVfx != null)
            foreach (ParticleSystem ps in chargedVfx.GetComponentsInChildren<ParticleSystem>())
                ps.Stop(false, ParticleSystemStopBehavior.StopEmitting);

        if (handSmoke != null)
        {
            // Odczepiamy od pocisku, żeby resztki dymu dogasły przy dłoni (Stop Action = Destroy)
            handSmoke.transform.SetParent(null, true);
            handSmoke.Stop(true, ParticleSystemStopBehavior.StopEmitting);
        }

        if (currentSpellColliders != null)
            foreach (Collider c in currentSpellColliders)
                if (c != null) c.enabled = true;

        if (currentSpellRb != null)
        {
            currentSpellRb.isKinematic = false;
            Vector3 shootDir = spellSpawnPoint != null ? spellSpawnPoint.forward : transform.forward;
            currentSpellRb.linearVelocity = shootDir * spellShootForce;
        }

        // Od teraz pociskiem zarządza spell_new (licznik życia, uderzenie, zanikanie)
        if (currentSpellLogic != null)
            currentSpellLogic.Launch();

        ClearSpellReferences();
    }

    void ClearSpellReferences()
    {
        currentSpellInstance = null;
        currentSpellRb = null;
        currentSpellColliders = null;
        currentSpellLogic = null;
        chargeVfxTransform = null;
        chargeVfxBaseScale = Vector3.one;
        tintedParticleSystems = new ParticleSystem[0];
        chargedVfx = null;
        handSmoke = null;
    }

    // Podgląd trajektorii w Scene view (zaznacz obiekt z tym skryptem)
    void OnDrawGizmosSelected()
    {
        if (spellSpawnPoint == null) return;

        Gizmos.color = Color.red;
        Vector3 prev = OffsetToWorld(GetChargeOffset(0f));
        for (int i = 1; i <= 40; i++)
        {
            Vector3 next = OffsetToWorld(GetChargeOffset(i / 40f));
            Gizmos.DrawLine(prev, next);
            prev = next;
        }

        // Dwa okrążenia: v1 i v2
        Gizmos.color = new Color(1f, 0.4f, 0.4f);
        prev = OffsetToWorld(GetLoopOffset(0f));
        for (int i = 1; i <= 120; i++)
        {
            Vector3 next = OffsetToWorld(GetLoopOffset(i / 120f * loopDuration * 2f));
            Gizmos.DrawLine(prev, next);
            prev = next;
        }

        Gizmos.color = new Color(0.6f, 0.6f, 0.6f);
        Gizmos.DrawWireSphere(OffsetToWorld(new Vector3(0f, -handSmokeDrop, 0f)), 0.03f);
    }
}
