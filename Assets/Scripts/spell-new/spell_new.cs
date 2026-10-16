using UnityEngine;
using System.Collections;
using FMODUnity;

public class spell_new : MonoBehaviour
{
    public GameObject chargeChild;
    public GameObject chargedChild;
    public GameObject groundChild;
       
    public float chargedChildActivationDelay = 0.5f;
    public float destroyDelay = 2f;
    public float colorChangeDelay = 2f;
    public float colorChangeDuration = 2f;

    // NOWA ZMIENNA: Numer warstwy, który chcemy sprawdzić
    private int environmentLayer;

    bool hasHitGround = false;
    Rigidbody rb;

    private AudioSystem audioSystem;

    private ParticleSystem chargePs;
    private ParticleSystem chargedPs;

    void Start()
    {
        rb = GetComponent<Rigidbody>();

        audioSystem = FindObjectOfType<AudioSystem>();

        // ZMIANA 2: Pobieramy numer warstwy 'Environment'
        // Uwaga: Jeśli warstwa 'Environment' nie istnieje, ta funkcja zwróci 0.
        environmentLayer = LayerMask.NameToLayer("Environment");
        
        // Jeśli chcemy być bardzo bezpieczni i wyłączyć kolizję w ogóle,
        // jeśli warstwa nie istnieje:
        if (environmentLayer == -1) 
        {
            Debug.LogError("POMOCNIK: Warstwa 'Environment' nie została znaleziona. Upewnij się, że jest zdefiniowana w Unity.");
        }


        if (chargeChild != null)
            chargePs = chargeChild.GetComponent<ParticleSystem>();
        if (chargedChild != null)
            chargedPs = chargedChild.GetComponent<ParticleSystem>();

        if (groundChild != null)
            groundChild.SetActive(false);
    }

    void OnCollisionEnter(Collision collision)
    {
        Debug.Log("Spell hit the ground.");

        if (hasHitGround) return;
        
        // ZMIANA 3: Sprawdzamy Layer obiektu kolidującego
        // collision.gameObject.layer zwraca numer warstwy
        if (collision.gameObject.layer == environmentLayer)
        {
            hasHitGround = true;

            ParticleSystem[] allParticleSystems = GetComponentsInChildren<ParticleSystem>();

            foreach (ParticleSystem ps in allParticleSystems)
            {
                ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            }

            if (groundChild != null)
                groundChild.SetActive(true);

            if (rb != null)
            {
                rb.useGravity = false;
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
            
            if (audioSystem != null)
            {
                audioSystem.SpellImpactSound(transform.position);
            }

            StartCoroutine(ChangeParticleColorAndDestroy(groundChild));
        }
    }

    private IEnumerator ChangeParticleColorAndDestroy(GameObject parent)
    {
        ParticleSystem[] particleSystems = parent.GetComponentsInChildren<ParticleSystem>();

        yield return new WaitForSeconds(colorChangeDelay);

        float elapsedTime = 1f;

        while (elapsedTime < colorChangeDuration)
        {
            elapsedTime += Time.deltaTime;
            float t = elapsedTime / colorChangeDuration;

            foreach (ParticleSystem ps in particleSystems)
            {
                var mainModule = ps.main;
                
                // POBIERZ AKTUALNY KOLOR
                Color startCol = mainModule.startColor.color;
                
                // ZDEFINIUJ KOLOR KOŃCOWY (TEN SAM KOLOR, ALE PRZEZROCZYSTY)
                Color targetCol = new Color(startCol.r, startCol.g, startCol.b, 0f); // Ostatni parametr (0f) to przezroczystość

                // ZMIANA: Interpoluj do przezroczystości (Alpha = 0)
                mainModule.startColor = Color.Lerp(startCol, targetCol, t);
            }

            yield return null;
        }
        Destroy(gameObject, destroyDelay);
    }
}