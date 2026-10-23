using UnityEngine;
using FMOD.Studio; // PLAYBACK_STATE
using FMODUnity;   // RuntimeManager, RuntimeUtils

// Rzucanie zaklęcia z kamery: ładowanie (LPM trzymany), wystrzał (puszczenie LPM),
// anulowanie (PPM albo puszczenie LPM przed naładowaniem).
public class spell_cast_new : MonoBehaviour
{
    [Header("Zaklęcie")]
    public GameObject spellPrefab;
    public Transform spellSpawnPoint;
    public float chargeTime = 0.5f;
    public float spellShootForce = 20f;

    [Header("Wygląd ładowania")]
    public Color chargeStartColor = Color.black;
    public Color chargeEndColor = new Color(1f, 0.6235f, 0f);
    public AnimationCurve chargeCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
    [Tooltip("Barwi też dym na roocie prefabu (tak działała stara wersja).")]
    public bool tintRootSmoke = false;

    [Header("Zapasowe nazwy dzieci (gdy prefab nie ma spell_new z przypisanymi polami)")]
    public string chargeChildFallbackName = "Charge_FX";
    public string chargedChildFallbackName = "Charged_FX";

    private enum SpellState { Idle, Charging, Holding }
    private SpellState currentState = SpellState.Idle;
    private float chargeTimer = 0f;

    // Referencje do aktualnie ładowanego pocisku
    private GameObject currentSpellInstance;
    private Rigidbody currentSpellRb;
    private Collider[] currentSpellColliders;
    private spell_new currentSpellLogic;
    private Transform chargeVfxTransform;
    private Vector3 chargeVfxBaseScale = Vector3.one;
    private ParticleSystem[] tintedParticleSystems = new ParticleSystem[0];
    private GameObject chargedVfx;

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
        }

        if (isAudioInitialized)
            audioSystem.SpellSound.set3DAttributes(RuntimeUtils.To3DAttributes(transform));
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

        FollowSpawnPoint();
        ApplyChargeVisuals(chargeCurve.Evaluate(t), t);

        if (t >= 1f)
        {
            currentState = SpellState.Holding;
            SetFullyChargedVisuals();
        }
    }

    void HandleHoldingState()
    {
        if (currentSpellInstance == null) { CancelCast(); return; }

        if (Input.GetMouseButtonDown(1)) { CancelCast(); return; }

        FollowSpawnPoint();

        if (!Input.GetMouseButton(0))
            ReleaseCast();
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

    void ReleaseCast()
    {
        FireSpell();

        if (isAudioInitialized)
            audioSystem.SpellRelease();

        ResetToIdle();
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
        currentState = SpellState.Idle;
    }

    // ---------------- POCISK ----------------

    bool SpawnSpell()
    {
        if (spellPrefab == null || spellSpawnPoint == null)
        {
            Debug.LogWarning("POMOCNIK: Brak spellPrefab albo spellSpawnPoint w spell_cast_new.");
            return false;
        }

        currentSpellInstance = Instantiate(spellPrefab, spellSpawnPoint.position, spellSpawnPoint.rotation);

        // Podczas ładowania fizyka nie rusza pocisku...
        currentSpellRb = currentSpellInstance.GetComponent<Rigidbody>();
        if (currentSpellRb != null)
            currentSpellRb.isKinematic = true;

        // ...a collidery są wyłączone, żeby nie trącał gracza ani otoczenia
        currentSpellColliders = currentSpellInstance.GetComponentsInChildren<Collider>();
        foreach (Collider c in currentSpellColliders)
            c.enabled = false;

        currentSpellLogic = currentSpellInstance.GetComponent<spell_new>();

        // Efekt ładowania: najpierw pole ze spell_new, w razie braku szukamy po nazwie
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
        }

        // Które particle barwimy podczas ładowania
        var tinted = new System.Collections.Generic.List<ParticleSystem>();
        if (chargeObj != null)
            tinted.AddRange(chargeObj.GetComponentsInChildren<ParticleSystem>());
        if (tintRootSmoke)
        {
            ParticleSystem rootPs = currentSpellInstance.GetComponent<ParticleSystem>();
            if (rootPs != null) tinted.Add(rootPs);
        }
        tintedParticleSystems = tinted.ToArray();
        SetStartColor(chargeStartColor);

        // Efekt "naładowany": wyłączony do momentu pełnego naładowania
        if (currentSpellLogic != null && currentSpellLogic.chargedChild != null)
            chargedVfx = currentSpellLogic.chargedChild;
        else
            chargedVfx = FindChild(chargedChildFallbackName);

        if (chargedVfx != null)
            chargedVfx.SetActive(false);

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

        currentSpellInstance.transform.SetPositionAndRotation(spellSpawnPoint.position, spellSpawnPoint.rotation);
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
    }
}
