import { Component, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { AuthService } from '../auth.service';

@Component({
  imports: [ReactiveFormsModule],
  selector: 'app-login',
  styleUrl: './login.css',
  templateUrl: './login.html',
})
export class Login {
  protected readonly form = new FormGroup({
    employeeId: new FormControl<number | null>(null, { validators: [Validators.required] }),
    // Day 122: required, like the employee id; nonNullable keeps its value a string.
    password: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
  });
  protected readonly errorMessage = signal<string | null>(null);

  constructor(
    private readonly authService: AuthService,
    private readonly router: Router,
  ) {}

  submit(): void {
    if (this.form.invalid) {
      return;
    }
    const { employeeId, password } = this.form.getRawValue();
    this.errorMessage.set(null);
    this.authService.login(employeeId!, password).subscribe({
      next: () => this.router.navigate(['/']),
      // Day 122: deliberately doesn't say WHICH one was wrong — the API
      // doesn't reveal that either (no employee enumeration).
      error: () => this.errorMessage.set('Login failed — check the employee id and password.'),
    });
  }
}
