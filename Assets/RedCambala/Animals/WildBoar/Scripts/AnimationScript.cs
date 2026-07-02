using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class AnimationScript : MonoBehaviour
{
    private Animator animator; // Ссылка на компонент Animator

    private void Start()
    {
        animator = GetComponent<Animator>(); // Получаем компонент Animator объекта
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.Alpha1)) // Проверяем нажатие клавиши "1"
        {
            StopAnimation();
            animator.SetTrigger("Idle");
        }

         if (Input.GetKeyDown(KeyCode.Alpha2)) // Проверяем нажатие клавиши "2"
        {
            StopAnimation();
            animator.SetTrigger("Walk");
        }

         if (Input.GetKeyDown(KeyCode.Alpha3)) // Проверяем нажатие клавиши "3"
        {
            StopAnimation();
            animator.SetTrigger("Run");
        }
         if (Input.GetKeyDown(KeyCode.Alpha4)) // Проверяем нажатие клавиши "4"
        {
            StopAnimation();
            animator.SetTrigger("Attack1");
        }
        if (Input.GetKeyDown(KeyCode.Alpha4) && Input.GetKey(KeyCode.LeftShift)) // Проверяем нажатие клавиши "4 + LeftShift"
        {
            StopAnimation();
            animator.SetTrigger("Attack2");
        }
         if (Input.GetKeyDown(KeyCode.Alpha5)) // Проверяем нажатие клавиши "5"
        {
            StopAnimation();
            animator.SetTrigger("Hit1");
        }
         if (Input.GetKeyDown(KeyCode.Alpha6)) // Проверяем нажатие клавиши "6"
        {
            StopAnimation();
            animator.SetTrigger("Hit2");
        }
         if (Input.GetKeyDown(KeyCode.Alpha7)) // Проверяем нажатие клавиши "7"
        {
            StopAnimation();
            animator.SetTrigger("Death1");
        }
         if (Input.GetKeyDown(KeyCode.Alpha8)) // Проверяем нажатие клавиши "8"
        {
            StopAnimation();
            animator.SetTrigger("Death2");
        }
         if (Input.GetKeyDown(KeyCode.Alpha9)) // Проверяем нажатие клавиши "9"
        {
            StopAnimation();
            animator.SetTrigger("FullStun");
        }

        if (Input.GetKeyDown(KeyCode.Space)) // Проверяем нажатие клавиши "Space"
        {
            StopAnimation();
        }
    }

    void StopAnimation()
    {
        animator.ResetTrigger("Idle"); // Сбрасываем триггер "IdleTrigger"
        animator.ResetTrigger("Walk");
        animator.ResetTrigger("Run");
        animator.ResetTrigger("Attack1");
        animator.ResetTrigger("Attack2");
        animator.ResetTrigger("Hit1");
        animator.ResetTrigger("Hit2");
        animator.ResetTrigger("Death1");
        animator.ResetTrigger("Death2");
        animator.ResetTrigger("FullStun");
        animator.Play("DefaultState"); // Переключаем аниматор в состояние по умолчанию
    }
}
