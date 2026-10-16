using UnityEngine;
using FMOD.Studio; // Wymagane dla PLAYBACK_STATE
using FMODUnity;   // Wymagane dla RuntimeManager
using System.Collections;
// Usunięto: using Unity.VisualScripting;

public class spell_cast_new : MonoBehaviour
{
    // --- KONFIGURACJA ZAKLĘCIA (WIZUALNA I FIZYCZNA) ---
    public float chargeTime = 0.5f;
    public GameObject spellPrefab;
    public Transform spellSpawnPoint;
    public float spellShootForce = 20f;
    public Color chargeStartColor = Color.black;
    public Color chargeEndColor = new Color(1f, 0.6235f, 0f);
    public string chargedChildName = "ChargedFX";
    public AnimationCurve chargeCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

    // --- ZMIENNE PRYWATNE (STAN) ---
    private float chargeTimer = 0f;
    private float scaleValue = 0f;
    private SpellState currentState = SpellState.Idle;
    
    // Referencje do obiektu pocisku
    private GameObject currentSpellInstance;
    private Rigidbody currentSpellRb;
    private Transform currentVfxTransform;
    private ParticleSystem currentParticleSystem;
    private GameObject chargedChildInstance;

    // --- INTEGRACJA AUDIO FMOD ---
    private AudioSystem audioSystem;
    private PLAYBACK_STATE audioPbState;
    private const string STOPPED_STATE = "STOPPED"; 
    private bool isAudioInitialized = false; // FLAGA BEZPIECZEŃSTWA

    enum SpellState
    {
        Idle,
        Charging,
        Holding
    }

    // Wykonuje się przed Start() - idealne do pobierania referencji do singletonów
    void Awake() 
    {
        // 1. POBIERANIE AUDIO SYSTEM
        audioSystem = FindObjectOfType<AudioSystem>();

        if (audioSystem == null)
        {
            Debug.LogWarning("POMOCNIK: Wymagany skrypt AudioSystem nie został znaleziony na scenie. Kontynuuję bez audio.");
            return; 
        }

        // 2. TWORZENIE INSTANCJI DŹWIĘKU 
        // POPRAWKA BŁĘDU CS1061: Zamiast isValid() sprawdzamy, czy Path w EventReference jest pusty.
        if (!string.IsNullOrEmpty(audioSystem.spellEvent.Path))
        {
             audioSystem.SpellSound = RuntimeManager.CreateInstance(audioSystem.spellEvent);
            isAudioInitialized = true; 
        }
        else
        {
            Debug.LogWarning("POMOCNIK: Brak przypisanego FMOD Eventu dla zaklęcia lub Event jest niepoprawny. Kontynuuję bez audio.");
        }
    }

    void Update()
    {
        switch (currentState)
        {
            case SpellState.Idle:
                HandleIdleState();
                break;
            case SpellState.Charging:
                HandleChargingState();
                break;
            case SpellState.Holding:
                HandleHoldingState();
                break;
        }
        
        // Aktualizacja atrybutów 3D dla dźwięku - wywołuj tylko, jeśli jest inicjalizacja
        if (isAudioInitialized)
            audioSystem.SpellSound.set3DAttributes(FMODUnity.RuntimeUtils.To3DAttributes(transform));
    }

    void HandleIdleState()
    {
        if (Input.GetMouseButtonDown(0))
        {
            bool shouldStartCharging = true;
            
            if (isAudioInitialized)
            {
                // --- LOGIKA AUDIO: SPRAWDZENIE STANU ODTWARZANIA ---
                audioSystem.SpellSound.getPlaybackState(out audioPbState);
                
                // Kontynuuj ładowanie tylko, jeśli dźwięk jest zatrzymany (i audio działa)
                if (audioPbState.ToString() != STOPPED_STATE) 
                {
                    shouldStartCharging = false;
                }
            }
            
            if (shouldStartCharging) 
            {
                chargeTimer = 0f;
                currentState = SpellState.Charging;
                SpawnSpell();
                
                // --- LOGIKA AUDIO: ODPALENIE DŹWIĘKU (tylko jeśli jest inicjalizacja) ---
                if (isAudioInitialized)
                    audioSystem.SpellCast();
            }
        }
    }

    void HandleChargingState()
    {
        if (Input.GetMouseButton(0))
        {
            chargeTimer += Time.deltaTime;
            FollowSpawnPoint();
            float t = Mathf.Clamp01(chargeTimer / chargeTime);
            scaleValue = chargeCurve.Evaluate(t);
            UpdateChargingVfx(t);

            if (chargeTimer >= chargeTime && currentState != SpellState.Holding)
            {
                currentState = SpellState.Holding;
                SetFullyChargedVfx();
            }
        }

        if (Input.GetMouseButtonUp(0))
        {
            if (chargeTimer >= chargeTime)
            {
                FireSpell("released at end of CHARGING");
            }
            else
            {
                CancelSpell();
                // --- LOGIKA AUDIO: Anulowanie przy wczesnym puszczeniu ---
                if (isAudioInitialized)
                    audioSystem.SpellCancel();
            }
            
            // --- LOGIKA AUDIO: Zatrzymanie głównego dźwięku ładowania po każdym zakończeniu ---
            if (isAudioInitialized)
                audioSystem.SpellRelease();

            chargeTimer = 0f;
            currentState = SpellState.Idle;
        }

        // Dodatkowy warunek do obsługi anulowania przez PPM
        if (Input.GetMouseButtonDown(1))
        {
            CancelSpell();
            
            // --- LOGIKA AUDIO: Anulowanie PPM (poprawka logiki) ---
            if (isAudioInitialized)
            {
                audioSystem.SpellCancel(); 
                audioSystem.SpellRelease();
            }
                
            
            chargeTimer = 0f;
            currentState = SpellState.Idle;
        }
    }

    void HandleHoldingState()
    {
        FollowSpawnPoint();
        scaleValue = 1f;

        if (Input.GetMouseButtonUp(0))
        {
            FireSpell("released during HOLDING");
            
            // --- LOGIKA AUDIO: ZATRZYMANIE DŹWIĘKU ---
            if (isAudioInitialized)
                audioSystem.SpellRelease();
            
            chargeTimer = 0f;
            currentState = SpellState.Idle;
        }

        if (Input.GetMouseButtonDown(1))
        {
            // ANULOWANIE 2: Naciśnięcie PPM z naładowanym zaklęciem
            CancelSpell();
            
            // --- LOGIKA AUDIO: Anulowanie PPM (poprawka logiki) ---
            if (isAudioInitialized)
            {
                audioSystem.SpellCancel(); 
                audioSystem.SpellRelease();
            }

            chargeTimer = 0f;
            currentState = SpellState.Idle;
        }
    }

    void SpawnSpell()
    {
        if (spellPrefab == null || spellSpawnPoint == null) return;

        currentSpellInstance = Instantiate(spellPrefab, spellSpawnPoint.position, spellSpawnPoint.rotation);
        
        // ... (Reszta logiki SpawnSpell bez zmian) ...
        currentSpellRb = currentSpellInstance.GetComponent<Rigidbody>();
        if (currentSpellRb != null)
            currentSpellRb.isKinematic = true;

        currentParticleSystem = currentSpellInstance.GetComponentInChildren<ParticleSystem>();
        if (currentParticleSystem != null)
        {
            currentVfxTransform = currentParticleSystem.transform;
            currentVfxTransform.localScale = Vector3.zero;
            var main = currentParticleSystem.main;
            main.startColor = chargeStartColor;
        }

        chargedChildInstance = null;
        if (!string.IsNullOrEmpty(chargedChildName))
        {
            Transform child = currentSpellInstance.transform.Find(chargedChildName);
            if (child != null)
            {
                chargedChildInstance = child.gameObject;
                chargedChildInstance.SetActive(false);
            }
        }
    }

    void FollowSpawnPoint()
    {
        if (currentSpellInstance == null || spellSpawnPoint == null) return;

        currentSpellInstance.transform.position = spellSpawnPoint.position;
        currentSpellInstance.transform.rotation = spellSpawnPoint.rotation;
    }

    void UpdateChargingVfx(float t)
    {
        if (currentVfxTransform != null)
            currentVfxTransform.localScale = Vector3.one * scaleValue;

        if (currentParticleSystem != null)
        {
            var main = currentParticleSystem.main;
            main.startColor = Color.Lerp(chargeStartColor, chargeEndColor, t);
        }
    }

    void SetFullyChargedVfx()
    {
        if (currentVfxTransform != null)
            currentVfxTransform.localScale = Vector3.one;

        if (currentParticleSystem != null)
        {
            var main = currentParticleSystem.main;
            main.startColor = chargeEndColor;
        }

        if (chargedChildInstance != null)
            chargedChildInstance.SetActive(true);
    }

    void FireSpell(string reason)
    {
        if (currentSpellInstance == null) return;

        // Odpięcie od rodzica i wystrzelenie
        if (currentSpellRb != null)
        {
            currentSpellInstance.transform.SetParent(null);
            currentSpellRb.isKinematic = false;

            Vector3 shootDir = spellSpawnPoint != null ? spellSpawnPoint.forward : transform.forward;
            currentSpellRb.linearVelocity = shootDir * spellShootForce;
        }
        
        // Zniszczenie obiektu po czasie życia pocisku (zdefiniowane w innym skrypcie/prefabie)
        // Zakładamy, że to już obsługuje Spell_new.cs lub inna logika

        currentSpellInstance = null;
        currentSpellRb = null;
        currentParticleSystem = null;
        currentVfxTransform = null;
        chargedChildInstance = null;
    }

    void CancelSpell()
    {
        if (currentSpellInstance != null)
            Destroy(currentSpellInstance);

        currentSpellInstance = null;
        currentSpellRb = null;
        currentParticleSystem = null;
        currentVfxTransform = null;
        chargedChildInstance = null;
    }
}