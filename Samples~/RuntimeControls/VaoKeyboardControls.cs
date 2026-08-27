using Modavis.Vao;
using UnityEngine;

public sealed class VaoKeyboardControls : MonoBehaviour
{
    [SerializeField] private VaoSamplePlayer player;
    [SerializeField] private VaoLinkedAnimationPlayer animations;
    [SerializeField] private int baseMidiNote = 60;

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.A)) Press(baseMidiNote);
        if (Input.GetKeyUp(KeyCode.A)) Release(baseMidiNote);
        if (Input.GetKeyDown(KeyCode.W)) Press(baseMidiNote + 1);
        if (Input.GetKeyUp(KeyCode.W)) Release(baseMidiNote + 1);
        if (Input.GetKeyDown(KeyCode.S)) Press(baseMidiNote + 2);
        if (Input.GetKeyUp(KeyCode.S)) Release(baseMidiNote + 2);
    }

    private void Press(int note) { player?.NoteOn(note); animations?.NoteOn(note); }
    private void Release(int note) { player?.NoteOff(note); animations?.NoteOff(note); }
}
