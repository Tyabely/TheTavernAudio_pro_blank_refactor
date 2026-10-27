using UnityEngine;
using System.Collections;
using System.Collections.Generic;

// Logika samego pocisku: lot, uderzenie w ziemię, zanikanie i zniszczenie.
// Siedzi na roocie prefabu VFX_BlackSmoke.
[RequireComponent(typeof(Rigidbody))]
public class spell_new : MonoBehaviour
{
    [Header("Dzieci VFX (przypisane w prefabie)")]
    public GameObject chargeChild;   // Charge_FX - orb: skalowany i barwiony podczas ładowania
    public GameObject chargedChild;  // Charged_FX - ciemnoszary dym, włączany po pełnym naładowaniu
    public GameObject groundChild;   // Ground_Impact_FX - włączane po uderzeniu
    public GameObject handSmokeChild; // Hand_Smoke_FX - czarny dym spod dłoni, sterowany przez spell_cast_new
    public GameObject impactBurstChild; // Impact_Burst_FX - krótki wybuch w punkcie uderzenia

    [Header("Czasy")]
    [Tooltip("Po ilu sekundach lotu bez trafienia pocisk się niszczy.")]
    public float maxLifetime = 5f;
    [Tooltip("Ile sekund po uderzeniu zaczyna się zanikanie.")]
    public float colorChangeDelay = 2f;
    [Tooltip("Jak długo trwa zanikanie efektu uderzenia.")]
    public float colorChangeDuration = 2f;
    [Tooltip("Ile sekund po zaniknięciu obiekt jest niszczony.")]
    public float destroyDelay = 2f;

    [Header("Kolizja")]
    public string environmentLayerName = "Environment";

    private int environmentLayer = -1;
    private bool hasHitGround = false;
    private bool isLaunched = false;
    private float flightTimer = 0f;

    private Rigidbody rb;
    private AudioSystem audioSystem;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();

        // Wyłączamy efekt uderzenia już w Awake (a nie w Start),
        // żeby nie mignął przez jedną klatkę po Instantiate.
        if (groundChild != null)
            groundChild.SetActive(false);
        if (impactBurstChild != null)
            impactBurstChild.SetActive(false);

        // Root ParticleSystem miał Stop Action = Destroy, przez co po zatrzymaniu
        // dymu Unity samo niszczyło CAŁY pocisk (razem z efektem uderzenia).
        // O czasie życia decyduje teraz ten skrypt.
        ParticleSystem rootPs = GetComponent<ParticleSystem>();
        if (rootPs != null)
        {
            var main = rootPs.main;
            main.stopAction = ParticleSystemStopAction.None;
        }

        // NameToLayer zwraca -1, jeśli warstwa nie istnieje.
        environmentLayer = LayerMask.NameToLayer(environmentLayerName);
        if (environmentLayer == -1)
            Debug.LogError($"POMOCNIK: Warstwa '{environmentLayerName}' nie istnieje. Dodaj ją w Tags and Layers.");
    }

    void Start()
    {
        audioSystem = FindFirstObjectByType<AudioSystem>();
    }

    // Wywoływane przez spell_cast_new w momencie wystrzału.
    // Licznik życia startuje dopiero teraz, żeby pocisk nie zniknął w dłoni podczas ładowania.
    public void Launch()
    {
        isLaunched = true;
        flightTimer = 0f;
    }

    void Update()
    {
        if (!isLaunched || hasHitGround) return;

        flightTimer += Time.deltaTime;
        if (flightTimer >= maxLifetime)
            Destroy(gameObject);
    }

    void OnCollisionEnter(Collision collision)
    {
        if (hasHitGround) return;
        if (collision.gameObject.layer != environmentLayer) return;

        hasHitGround = true;
        Debug.Log("Spell hit the ground.");

        // Zatrzymujemy emisję wszystkich AKTYWNYCH particli (dym, charge, charged).
        // groundChild jest jeszcze nieaktywny, więc nie zostanie zatrzymany.
        foreach (ParticleSystem ps in GetComponentsInChildren<ParticleSystem>())
            ps.Stop(false, ParticleSystemStopBehavior.StopEmitting);

        // Zatrzymanie pocisku na sztywno.
        if (rb != null)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.useGravity = false;
            rb.isKinematic = true;
        }

        if (groundChild != null)
            groundChild.SetActive(true);

        // Wybuch w faktycznym punkcie styku (collider nie musi leżeć w środku orba),
        // skierowany wzdłuż normalnej powierzchni
        if (impactBurstChild != null)
        {
            ContactPoint contact = collision.GetContact(0);
            impactBurstChild.transform.SetPositionAndRotation(
                contact.point, Quaternion.LookRotation(contact.normal));
            impactBurstChild.SetActive(true);
        }

        if (audioSystem != null)
            audioSystem.SpellImpactSound(transform.position);

        StartCoroutine(FadeOutAndDestroy());
    }

    private IEnumerator FadeOutAndDestroy()
    {
        yield return new WaitForSeconds(colorChangeDelay);

        ParticleSystem[] systems = groundChild != null
            ? groundChild.GetComponentsInChildren<ParticleSystem>()
            : new ParticleSystem[0];

        // Przestajemy emitować nowe cząsteczki, a istniejące płynnie wygaszamy.
        foreach (ParticleSystem ps in systems)
            if (ps != null)
                ps.Stop(false, ParticleSystemStopBehavior.StopEmitting);

        // Oryginalna alfa każdej cząsteczki (klucz: randomSeed cząsteczki),
        // żeby zanikanie było liniowe i nie kumulowało błędów zaokrągleń.
        var originalAlpha = new Dictionary<uint, byte>[systems.Length];
        for (int i = 0; i < systems.Length; i++)
            originalAlpha[i] = new Dictionary<uint, byte>();

        ParticleSystem.Particle[] buffer = new ParticleSystem.Particle[0];
        float duration = Mathf.Max(0.0001f, colorChangeDuration);
        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float factor = 1f - Mathf.Clamp01(elapsed / duration);

            for (int i = 0; i < systems.Length; i++)
                FadeParticles(systems[i], factor, originalAlpha[i], ref buffer);

            yield return null;
        }

        Destroy(gameObject, destroyDelay);
    }

    private static void FadeParticles(ParticleSystem ps, float factor,
        Dictionary<uint, byte> originalAlpha, ref ParticleSystem.Particle[] buffer)
    {
        // Część efektów ma własne Stop Action = Destroy, więc mogą już nie istnieć.
        if (ps == null) return;

        int count = ps.particleCount;
        if (count == 0) return;

        if (buffer.Length < count)
            buffer = new ParticleSystem.Particle[count];

        int alive = ps.GetParticles(buffer);
        for (int p = 0; p < alive; p++)
        {
            uint key = buffer[p].randomSeed;
            Color32 c = buffer[p].startColor;

            if (!originalAlpha.TryGetValue(key, out byte baseAlpha))
            {
                baseAlpha = c.a;
                originalAlpha[key] = baseAlpha;
            }

            c.a = (byte)Mathf.RoundToInt(baseAlpha * factor);
            buffer[p].startColor = c;
        }
        ps.SetParticles(buffer, alive);
    }
}
